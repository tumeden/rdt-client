# Automatic removal of failed jobs

Setting: **qBittorrent / *darr -> Delete download when in error** (minutes, `0` turns it off).

- It applies to every failed job, including jobs already in the list, as soon as it is saved. A job with its own value
  above 0 uses that value instead.
- Jobs added by Sonarr/Radarr no longer store a copy of the setting. Jobs added by an older version may still hold a
  copied value. Set those to 0 to make them follow the main setting.
- Jobs added in the web UI, the watch folder or by auto import follow it too when their own value is 0.
- Stalled jobs are failed whether or not removal is on, and are then removed like any other failed job.

## What is removed, in this order

1. The TorBox (provider) job that this rdt-client tracks.
2. Files rdt-client itself downloaded for the job, and folders left empty by that. Extracted files and anything else
   in the folder stay.
3. The list entry. If step 1 or 2 fails, the job stays listed and is tried again.

## Safeguards

- Retries, submission, provider sync, adding a job, changing a category and changing a job's settings all wait while
  a job is cleaned up. Everything is checked again before the first delete, and once more when the entry is deleted.
- The provider job is kept when another listed job uses it, comparing both provider id and hash, and when the job
  belongs to another provider.
- TorBox jobs are looked up first. If the list is unreadable, incomplete or ambiguous, nothing is deleted.
- Files are kept when another listed job has the same release name or an overlapping folder, or when the folder is not
  inside the download path. A job with a running local download or unpack is left alone.

## Provider load

- One job per 30 seconds. On TorBox that is one list request and one delete request, without fast retries. Other
  providers use their existing delete call.
- Any failure pauses all removals for 5 minutes. The failing job then waits 5, 10, 20, 40, 60 minutes (max 60).
- A rate limit pauses all removals for at least as long as the provider asks.

## Limits

- Jobs in a second rdt-client that shares the TorBox account cannot be seen.
- A job that is only in the TorBox queue is not in the job list, so it counts as already gone and stays queued there.
- A failed job that the provider keeps refusing to delete stays in the list. Delete it by hand.
- Manual deletes, job retries and deletes requested by Sonarr/Radarr still use the older delete code.
- Adding a job can wait up to about 20 seconds behind one TorBox delete.
- A retry or setting change made while a TorBox delete is already in flight cannot recall that request. The job and
  local files are kept if the change is detected before local file cleanup.

Keep the delay long enough for Sonarr/Radarr to see the failure first. 30 minutes is plenty.
