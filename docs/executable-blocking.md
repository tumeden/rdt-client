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

## What "blocked" means

- The job gets `Completed` plus an error that starts with `Blocked: contains executable files (...)`. Retries are
  used up and no retry is scheduled, so it is never resubmitted.
- The SABnzbd history shows it as **Failed**, so Sonarr's failed-download handling can blocklist the release and
  Seekarr/Sonarr can grab another one. The qBittorrent API reports `error`.
- Nothing is executed, opened or deleted. Provider jobs stay in place. The existing "Delete download when in
  error" setting (default 0) still applies as before.
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
