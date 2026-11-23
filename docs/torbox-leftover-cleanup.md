# TorBox leftover cleanup

These settings are under Provider and apply to TorBox only:
- **TorBox leftover cleanup:** `Off` (the default), `Report only`, or `Remove from TorBox`.
- **TorBox leftover minimum age (hours):** default 24, minimum 6.

## What counts as a leftover

A job on the TorBox account, torrent or Usenet, that meets all three conditions:
1. rdt-client does not track it. No local job has its TorBox hash or id as `RdId`, and no local `Hash` matches it.
2. TorBox reports it explicitly as `active: false`. Jobs with a missing or unknown `active` value are never treated as leftovers.
3. It was seen as an inactive, untracked job on every hourly check for at least the minimum age. If it becomes
   active or tracked, disappears, or the checks have a gap of more than 3h, the timer starts again.

## Safety

- It is checked hourly. Each check makes two single-attempt list requests (`torrents/mylist` and `usenet/mylist`)
  using the no-retry client. The run time is saved before any request, Retry-After and the shared cooldowns are
  honoured, and errors back off for 1h, 2h, 4h and so on.
- Local jobs are read after the TorBox lists are fetched, and read again right before any action. Anything
  rdt-client has started tracking in the meantime is left alone.
- At most 10 removals per hourly run. On the first error or rate limit, the run stops.
- If the rdt-client database has no jobs at all (for example a fresh or restored database), nothing is
  reported or removed.
- It is turned off while "Automatically import" is on, because rdt-client would import those jobs anyway.
- Only the TorBox job is deleted (`usenet/controlusenetdownload` or `torrents/controltorrent`, operation `delete`).
  Files already on your host are never touched.
- `Report only` logs a warning for each job it would remove, at most once a day per job, and changes nothing.

## Important: shared TorBox accounts

If another app, or the **second rdt-client (RDTClient1, port 6501)**, uses the same TorBox account, its jobs look
like leftovers to this instance and **will be removed** in `Remove from TorBox` mode. In that case, keep this
`Off` or `Report only`.

Recommended rollout: set `Report only` for a day, check the `TorBox cleanup (report only): would remove ...`
log lines, then switch to `Remove from TorBox`.

## Tests

`Services/TorBoxCleanup/*` covers:
- removal only after the minimum age
- jobs rdt-client tracks never being touched, matched by hash, by id, and by the legacy hash in `RdId`
- active or unknown jobs never being removed
- the timer starting again when a job becomes active
- report-only mode never deleting
- each way of being turned off making no requests
- an empty database removing nothing
- the cap of 10 per run
- a job that becomes tracked just before deletion being spared
- unreadable responses and rate limits, the hourly schedule, the run time being saved before requests, and long gaps
- the delete request URL, body and auth header being correct, with one attempt and failures raising an error

Mutation checks: removing the "tracked job" check or the empty-database check makes the tests fail.
