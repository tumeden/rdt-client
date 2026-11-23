# TorBox stalled NZB slot starvation: fix, watchdog, build and rollback notes

Status: development only. Nothing in this branch has been deployed. Deploying to production, or
recreating any container, needs separate approval from the owner.

- Base: `rogerfar/rdt-client` `a669bd3677d07d631fe09557fab425590cfa7a8e` (v2.0.143). On 2026-11-23 upstream
  `main` had no commits after this revision.
- Fork/branch: `tumeden/rdt-client`, branch `fix/torbox-stalled-slot-starvation`.

## The defect

1. `TorBoxDebridClient.UpdateData` maps TorBox `download_state=processing` to `TorrentStatus.Downloading`
   unless `download_present` is true. A job TorBox reports as processing, 100%, `download_finished=true`,
   `active=false`, `download_present=false` therefore stays `RdStatus=Downloading` with no time limit.
2. `TorrentRunner` counted provider slots with `RdStatus is not (Queued or Finished or Error)`. It did **not**
   exclude torrents that were already terminal locally (`Completed`/`Error` set). A job that expired through the
   built-in lifetime (which sets `Completed` + `Error` but leaves `RdStatus=3`) kept its slot for good. This
   matches the isolated container experiment, where expiry worked and the slot was never released.
3. With `MaxParallelDownloads=5`, five such jobs held every slot and 122 jobs that were never submitted
   (`RdStatus=0`, `RdId`/`RdAdded` NULL) were starved.

## Changes

| File | Change |
|---|---|
| `server/RdtClient.Service/Services/TorrentRunner.cs` | New `OccupiesProviderSlot(Torrent)`: torrents with `Completed` or `Error` set never take a slot. Otherwise the upstream status rule is unchanged. Applies to every provider. |
| `server/RdtClient.Service/Services/DebridClients/TorBoxDebridClient.cs` | New `GetUsenetSnapshot()`: one rate-limit-aware `mylist` request that returns the raw `active` / `download_present` / `download_finished` flags. |
| `server/RdtClient.Data/Models/Internal/DbSettings.cs` | New setting `Provider:StalledNzbTimeoutHours` (default 6, minimum 6, `0` turns it off). It shows up in the Settings UI under Provider. |
| `server/RdtClient.Service/Services/StalledJobs/*` | Watchdog: pure evaluator, persisted state store, TorBox snapshot source, run orchestration. |
| `server/RdtClient.Service/BackgroundServices/StalledJobWatchdogService.cs` | Hosted service. Waits 10 minutes after startup, wakes every 5 minutes, and runs only when the hourly slot is due. |
| `server/RdtClient.Service/DiConfig.cs` | DI registration. |
| `server/RdtClient.Service.Test/...` | 77 new tests (see below). |

The TorBox status mapping was left alone on purpose. Mapping `processing` to `Finished` would make these jobs
look successful, and mapping it to `Error` would fail jobs that are genuinely post-processing.

## Why a native watchdog instead of a companion

A companion service would have to go through supported APIs. `TorrentData.Update(Torrent)` (the API update
path) does not accept `RdStatus`, `Completed` or `Error`. The only supported way to fail a job from outside is
`Lifetime`, and lifetime counts from local `Added`, which includes time spent in the local queue. Using it on
the backlog is exactly what we must not do. The native code stays small. It reuses the lifetime expiry's own
terminal path (`UpdateRetry(id, null, TorrentRetryAttempts)` and then `UpdateComplete(id, reason, now, retry:false)`),
so it adds no new kind of state.

## Watchdog rules

Cadence and provider load
- At most one evaluation per hour. The hour is enforced from persisted `LastRunAt`, so a restart cannot
  trigger a catch-up burst. After any amount of downtime it runs once.
- Each run makes one shared `GET usenet/mylist` request. It never makes per-job provider requests. If there
  are no candidate jobs it makes no request at all.
- On HTTP 429 or `slow_down` it backs off for `max(Retry-After, 1h)`. It also respects the shared
  `api.torbox.app` cooldown in `RateLimitCoordinator`. Other errors, or a null payload, back off for
  1h, 2h, 4h, 8h, then 12h. It never retries quickly.
- Existing provider polling settings (`CheckInterval` and others) are unchanged.

Candidate (local), all of these must hold
- `Type=Nzb`, `ClientKind` TorBox or null, provider setting is TorBox.
- `RdId` and `RdAdded` are present, and `RdStatus` is `Processing` or `Downloading`. **Local queued or
  unsubmitted jobs are never candidates, however old `Added` is.**
- `Completed`, `Error` and `Retry` are null, and there are no `Downloads` rows (so no local transfer).

Qualifying provider condition. The match is local `RdId` = provider **hash**, case-insensitive, never the
numeric id.
- `download_state == "processing"`, `progress >= 1.0` (and finite), `download_finished == true`,
  `active == false`, `download_present == false`.
- Entries with a null or blank hash are ignored. Duplicate hashes are treated as ambiguous and skipped. A
  missing record, or any field that doesn't qualify, drops the observation, so the stall clock starts over.

Stall age
- A fingerprint of state, progress, the three flags, size and `updated_at` is stored for each job. Any change
  resets the observed stall start to "now".
- Normal path: the same fingerprint has been seen across at least 2 observations spanning **6h or more**.
- Retroactive path, for jobs that were already stuck: `updated_at` is present, not in the future, not before
  `created_at`, and **at least 24h old** (the 6h threshold plus an 18h margin, because TorBox timestamp
  timezone handling is uncertain by up to ±14h). In addition, our own observations must show the record
  unchanged for at least 55 minutes. In practice an already-stuck job is failed at the second hourly run.
  Local `Added` and provider `created_at` are never used as evidence.
- Continuity: if the clock moves backwards, or there is a gap of more than 3h between successful snapshots,
  all observations are discarded and no job is failed that run.

Mutation safety
- Before mutating, the job is re-read from the DB only. It must still be a candidate with the same `RdId`
  and no active download or unpack client.
- Jobs with `DeleteOnError > 0` are **skipped** with a warning, because the existing error handling would
  later delete the provider job and local files.
- No more than 10 jobs are failed per run. The rest are deferred.
- Nothing is deleted. No retry or resubmission is scheduled (`Retry` stays null and `RetryCount` is set to
  the maximum). Re-import cannot happen because the local row keeps its `RdId`.
- The error text, which is shown in the UI and through the SAB/qBit APIs, starts with `Stalled at TorBox:` and
  includes the evidence (observed hours or `updated_at`) plus "Provider job and files were not deleted."
- Logging: state changes and actionable failures are logged at Warning. Routine runs log at Debug only, so
  there are no success notifications. Logs contain no secrets.
- Only one run can happen at a time (`SemaphoreSlim`). An overlapping run is rejected.
- State lives at `<db dir>/stalled-job-watchdog.json`, which is `/data/db/` in the container and
  `/opt/media-apps/appdata/rdtclient2/db/` on CT530. Writes are atomic. A corrupt file is read as empty state,
  which can only delay failures, never cause them.

### Which existing stuck jobs would qualify

Going by the incident data (not re-verified live here): the 5 slot-holding jobs and the 4 later jobs that were
`processing`/100%/inactive/not-present with provider timestamps unchanged for days would be failed at the
**second** hourly run after deployment (about 1h10m after startup), and only if all of the following hold:
(a) TorBox still returns them in `mylist` with that exact shape, (b) `updated_at` is at least 24h old and
plausible, (c) nothing changes between the two runs, (d) `DeleteOnError` is 0 for them, and (e) they have no
`Downloads` rows. Any job without a usable `updated_at` waits 6 observed hours. The 122 unsubmitted jobs
are never candidates.

## Tests (synthetic fixtures only, no network)

Command (from `server/`):

```
dotnet test RdtClient.sln
```

Result on this branch (Windows, .NET SDK 10.0.403, 2026-11-23): `RdtClient.Service.Test` 346 passed, 0 failed
(baseline 269 plus 77 new). `RdtClient.Web.Test` 37 passed, 0 failed.

Mutation check: with the old slot predicate restored, `WatchdogFailure_ReleasesSlot_AndQueuedJobAdvances...` and
`BuiltInLifetimeExpiry_TerminalJobWithActiveLookingStatus_NoLongerBlocksQueue` both fail. With the fix they pass.

Coverage:
- `Regression/TorBoxStalledSlotStarvationTests`: real SQLite `TorrentData`, real `TorrentRunner.Tick`, real
  `TorBoxDebridClient` (only `GetClient` stubbed, strict mocks, so no outbound request is possible), real
  watchdog and real `Sabnzbd`. These cover starvation reproduced, then failure, slot release and the queued job
  submitted, with no retry, no `ControlAsync` delete and no re-add over later ticks. The failed job shows as
  `Failed` in SAB history and is absent from the SAB queue. They also cover the genuinely active job keeping its
  slot, the built-in lifetime path now releasing the slot, and `MaxParallelDownloads=0`.
- `Services/StalledJobs/StalledJobEvaluatorTest`: old unsubmitted queue untouched, the 6h timeout, a
  progressing job resetting the timer, a new job not failed, every non-qualifying provider shape, every excluded
  local state, missing records, empty or malformed or duplicate payloads, hash-not-id matching, the retroactive
  criteria (including future, missing and before-created timestamps, and not using Added/CreatedAt), clock
  going backwards, long gaps, the per-run cap and threshold clamping.
- `Services/StalledJobs/StalledJobWatchdogTest`: hourly cadence, no catch-up burst, provider error backoff,
  null payload, Retry-After handling (short and long), shared cooldown, no request when there are no
  candidates, disabled for other providers or with a 0 timeout or without a key, 122-job backlog untouched,
  the stale-observation race, the DeleteOnError skip, overlapping runs, restart with the file store, and a
  corrupt state file.
- `Services/TorrentRunnerTest`: the slot predicate for every status, both types, and terminal torrents.

## Building a pinned image (not done here: Docker is not available on the dev PC)

```
git clone https://github.com/tumeden/rdt-client.git && cd rdt-client
git checkout <commit sha of this branch>          # pin by SHA, not branch name
docker build --pull=false \
  --build-arg VERSION=v2.0.143-tumeden-stallfix.1 \
  --build-arg BUILD_DATE=$(date -u +%Y-%m-%dT%H:%M:%SZ) \
  -t rdtclient:v2.0.143-tumeden-stallfix.1 .
docker image inspect rdtclient:v2.0.143-tumeden-stallfix.1 --format '{{.Id}}'   # record this image ID
```

The Dockerfile runs `dotnet test` during the build. Pull base images by digest if a fully reproducible build
is needed. Record the resulting image ID or digest as the build artifact identifier.

## Deployment outline (needs owner approval, not performed)

1. Make a fresh appdata backup next to the existing
   `/opt/media-apps/backups/rdt-stall-watchdog-20261120T221340Z/`, and keep both.
2. Keep the original image available: `docker tag rogerfar/rdtclient:latest rdtclient:upstream-v2.0.143-a669bd3`
   (confirm by its image ID first).
3. In `/opt/media-apps/compose/rdtclient2/docker-compose.yml`, change only the `image:` line of `rdtclient2` to
   the pinned tag or ID. RDTClient1 (port 6501) is out of scope.
4. Recreate only `rdtclient2`. Watch the logs for `Stall watchdog` warnings for about 2 hours.

## Rollback

- Fast, no redeploy: set **Settings → Provider → Stalled NZB timeout** to `0`. The watchdog stops making
  requests and changing jobs on its next wake. The slot accounting fix stays active (it is harmless).
- Full: put the compose `image:` back to `rdtclient:upstream-v2.0.143-a669bd3` and recreate `rdtclient2`.
  The new setting row and `stalled-job-watchdog.json` are ignored by upstream and can stay.
- Jobs the watchdog failed are ordinary errored torrents (`Completed` + `Error`). They can be retried from the
  UI if wanted. Nothing was deleted, so nothing has to be restored.
- Note: on upstream, terminal jobs with an active-looking `RdStatus` would hold slots again.

## Limitations and open points

- Not run against the live TorBox API or the production DB. Every result above comes from synthetic fixtures.
- TorBox.NET models `active`, `download_present` and `download_finished` as non-nullable bools, so a missing
  field reads as `false`. A partial payload still cannot qualify unless state is `processing`, progress is
  1.0 and `download_finished` is true.
- The watchdog adds one `mylist` request per hour on top of ProviderUpdater's existing polling.
- When the job shows as `Failed` in SAB history, Sonarr/Seekarr may call the SAB delete API. What that deletes
  is the existing RDTClient/integration behaviour (`FinishedAction`, `del_files`) and was not changed here.
- The default of 6h applies as soon as the image is deployed. Set it to 0 before the first start if a
  staged enablement is preferred.
- Separate issue, not covered by this change and not treated as evidence either way: the 14 import-blocked
  releases containing `.exe` (MZ) payloads seen on 2026-11-20. Cleanup or filtering needs its own approval.
