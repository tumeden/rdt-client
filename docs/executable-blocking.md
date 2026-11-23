# Blocking executable payloads

Setting: **General → Block executable downloads** (`General:BlockExecutableDownloads`). It is **on by default**.

This addresses the 2026-11-20 incident, where releases that looked like TV episodes actually contained `.exe`
(MZ) files and Sonarr blocked them at import.

## What is blocked

Any job that contains a file ending in `.exe .scr .pif .bat .cmd .msi .msp .lnk .vbs .vbe .jse .wsf .wsh .ps1 .hta .cpl`,
including split volumes such as `setup.exe.001`. `.com` is left off the list on purpose, because it would match
domain names in NZB subjects. Par2 files and other non-executable files are not affected.

## Where it is checked (earliest first)

1. **When an NZB file is added** (Sonarr/Radarr upload NZBs through the SABnzbd API): the `<file subject>`
   names inside the NZB are inspected. If any is executable, the job is marked failed straight away and never
   sent to TorBox.
2. **Just before submission to the provider:** NZB files are checked again. This covers jobs queued before the
   setting was turned on, and a race between adding and the scheduler.
3. **When the provider has listed the job's files, before anything is downloaded to the host.** This covers
   magnets, NZB links and obfuscated NZBs whose real file names only appear after the provider processes them.
   That job has already used a provider slot by the time it is blocked.
4. **At download creation**, after rdt-client refreshes the job from the provider: the refreshed file list and the exact files about to be queued are checked again before any download row is created.
5. **At download start**, once the real file name is known: a single executable file is never started. That download is marked as errored, and the job then completes as failed.

## Mixed releases (video plus executable)

Setting: **General → Executable files in a release** (`General:ExecutableDownloadAction`).
- **Fail the whole job** (the default, and recommended): any executable fails the release, so Sonarr/Radarr grab a different one.
- **Skip only the executable files:** the rest of the release is downloaded and the executables are left out (a skipped file is marked complete with no error). A release that contains nothing but executables plus par2/nfo/sfv/txt/images is still failed. TorBox zipped downloads ("Prefer zipped downloads") cannot be split, so they are always failed.

## What "blocked" means

- The job gets `Completed` plus an error that starts with `Blocked: contains executable files (...)`. Retries are
  used up and no retry is scheduled, so it is never resubmitted.
- The SABnzbd history shows it as **Failed**, so Sonarr's failed-download handling can blocklist the release and
  Seekarr/Sonarr can grab another one. The qBittorrent API reports `error`.
- Nothing is executed or opened, and the block itself deletes nothing. However, like any other error, if "Delete download when in error" is above 0 (default 0), that existing setting later removes the job from rdt-client and the provider. Keep it at 0 if you want blocked jobs kept.
- Both quoted names and unquoted name-like text in NZB subjects are checked, so a payload cannot hide behind one quoted harmless name. TorBox file names without a folder are now kept (they used to map to an empty path).
- Jobs that already have local downloads are never interrupted.

## Tests

- `Helpers/ExecutableFileDetectorTest` covers extensions, NZB subject parsing, the domain false-positive and
  malformed NZBs.
- `Regression/ExecutableBlockingTests` uses real SQLite, the scheduler, the TorBox client (mocked API, no
  network) and the SABnzbd API. It covers blocking on add, never uploading the NZB, SAB `Failed`, clean NZBs
  still being submitted, the setting turned off, blocking at submission, provider file list blocking, and jobs
  that are already downloading.
- Mutation check: disabling the submission check or the provider-list check makes those tests fail.

## Limitations

- An NZB with obfuscated names is only caught at step 3, after TorBox has processed it.
- Detection works on file names only. An executable renamed to `.mkv` is not detected here, and Sonarr's import
  checks remain the backstop for that.
- Releases with a harmless executable bundled next to the video (rare on Usenet) are also blocked. Turn the
  setting off if that is a problem.

# Not retrying incomplete NZBs

Setting: **Provider → Don't retry incomplete NZBs** (`Provider:NoRetryIncompleteNzbs`). It is **on by default**.

When the provider reports an NZB error whose text means the articles are not available, the job is marked
failed straight away instead of being resubmitted. These texts count:
- `failed (Aborted, cannot be completed - https://sabnzbd.org/not-complete)`
- the TorBox `missing` state
- any message containing "cannot be completed", "missing articles" or "missing parts"

Normally a provider error deletes the provider job and re-adds the same NZB up to "Automatic retry torrent"
times, and that can never fix missing articles. With this setting the error text is kept, with
`Not retried: the NZB cannot be completed.` appended. Retries are used up, nothing is deleted, and SAB
history shows **Failed**, so Sonarr can pick another release straight away. Other provider errors, and
torrents, still use the normal retry setting.

Tests (`Regression/ExecutableBlockingTests`) cover each matching text, an unrelated error still retrying, the
setting turned off, torrents, the text-matching rules, and no re-add or provider delete across ticks. Mutation
check: disabling the rule makes the tests fail.
