using System.Globalization;
using RdtClient.Data.Enums;
using RdtClient.Data.Models.Data;

namespace RdtClient.Service.Services.StalledJobs;

/// <summary>
///     Pure, side-effect free decision logic for the TorBox stalled NZB watchdog.
///     Everything here fails closed: anything unexpected results in "no decision", never in a failure.
/// </summary>
public static class StalledJobEvaluator
{
    /// <summary>Evaluation / provider snapshot cadence. Hourly by requirement; never shorter.</summary>
    public static readonly TimeSpan EvaluationInterval = TimeSpan.FromHours(1);

    /// <summary>Lowest allowed stall threshold (one hourly check interval). The configured value is honored above this.</summary>
    public static readonly TimeSpan MinimumThreshold = TimeSpan.FromHours(1);

    /// <summary>
    ///     Retroactive path: extra margin added on top of the threshold before a provider updated_at timestamp is
    ///     trusted as stall-age evidence. Covers worst-case timezone interpretation of TorBox timestamps (±14h).
    /// </summary>
    public static readonly TimeSpan RetroactiveProviderAgeMargin = TimeSpan.FromHours(18);

    /// <summary>Retroactive path: minimum span of our own unchanged observations confirming the provider record is frozen.</summary>
    public static readonly TimeSpan RetroactiveConfirmationSpan = TimeSpan.FromMinutes(55);

    /// <summary>Gap between successful snapshots beyond which observed continuity is discarded.</summary>
    public static readonly TimeSpan MaxContinuityGap = TimeSpan.FromHours(3);

    public static readonly TimeSpan ClockSkewTolerance = TimeSpan.FromMinutes(5);

    /// <summary>Upper bound of jobs failed in one run; remaining eligible jobs are deferred to the next run.</summary>
    public const Int32 MaxFailuresPerRun = 10;

    public static TimeSpan? GetThreshold(Int32 configuredHours)
    {
        if (configuredHours <= 0)
        {
            return null;
        }

        var threshold = TimeSpan.FromHours(configuredHours);

        return threshold < MinimumThreshold ? MinimumThreshold : threshold;
    }

    /// <summary>
    ///     A job that was actually submitted to TorBox, is not terminal locally, and has no local download activity.
    ///     Local queued / unsubmitted jobs (RdId or RdAdded missing, RdStatus Queued) are never candidates.
    /// </summary>
    public static Boolean IsCandidate(Torrent torrent)
    {
        return torrent.Type == DownloadType.Nzb
               && torrent.ClientKind is null or Provider.TorBox
               && !String.IsNullOrWhiteSpace(torrent.RdId)
               && torrent.RdAdded != null
               && torrent.RdStatus is TorrentStatus.Processing or TorrentStatus.Downloading
               && torrent.Completed == null
               && String.IsNullOrWhiteSpace(torrent.Error)
               && torrent.Retry == null
               && torrent.Downloads.Count == 0;
    }

    /// <summary>processing, 100%, download finished, inactive and nothing downloadable present.</summary>
    public static Boolean IsStalledCondition(ProviderUsenetJob job)
    {
        return String.Equals(job.DownloadState, "processing", StringComparison.OrdinalIgnoreCase)
               && job.Progress is { } progress
               && Double.IsFinite(progress)
               && progress >= 1.0
               && job.DownloadFinished == true
               && job.Active == false
               && job.DownloadPresent == false;
    }

    public static String Fingerprint(ProviderUsenetJob job)
    {
        return String.Join("|",
                           job.DownloadState?.ToLowerInvariant() ?? "",
                           job.Progress?.ToString("R", CultureInfo.InvariantCulture) ?? "",
                           job.DownloadFinished?.ToString() ?? "",
                           job.Active?.ToString() ?? "",
                           job.DownloadPresent?.ToString() ?? "",
                           job.Size?.ToString(CultureInfo.InvariantCulture) ?? "",
                           job.UpdatedAt?.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) ?? "");
    }

    /// <summary>Provider updated_at is usable as evidence only when present, plausible and not in the future.</summary>
    public static Boolean IsTrustworthyProviderTimestamp(ProviderUsenetJob job, DateTimeOffset now)
    {
        if (job.UpdatedAt is not { } updatedAt)
        {
            return false;
        }

        if (updatedAt.Year < 2015 || updatedAt > now + ClockSkewTolerance)
        {
            return false;
        }

        if (job.CreatedAt is { } createdAt && createdAt.Year >= 2015 && updatedAt < createdAt - ClockSkewTolerance)
        {
            return false;
        }

        return true;
    }

    public static StalledJobEvaluation Evaluate(DateTimeOffset now,
                                                TimeSpan threshold,
                                                IEnumerable<Torrent> localTorrents,
                                                IEnumerable<ProviderUsenetJob?> snapshot,
                                                StalledJobState previous)
    {
        if (threshold < MinimumThreshold)
        {
            threshold = MinimumThreshold;
        }

        String? continuityReset = null;
        var previousObservations = previous.Observations;

        if (previous.LastSnapshotAt is { } lastSnapshot)
        {
            if (now < lastSnapshot - ClockSkewTolerance)
            {
                continuityReset = $"clock moved backwards (last snapshot {lastSnapshot:u}, now {now:u})";
            }
            else if (now - lastSnapshot > MaxContinuityGap)
            {
                continuityReset = $"gap of {(now - lastSnapshot).TotalHours:0.0}h since last successful snapshot";
            }
        }

        if (continuityReset != null)
        {
            previousObservations = new(StringComparer.OrdinalIgnoreCase);
        }

        // Index the provider snapshot by hash. Malformed entries are ignored, duplicate hashes are ambiguous.
        var byHash = new Dictionary<String, ProviderUsenetJob>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new HashSet<String>(StringComparer.OrdinalIgnoreCase);
        var malformed = 0;

        foreach (var job in snapshot)
        {
            if (job == null || String.IsNullOrWhiteSpace(job.Hash))
            {
                malformed++;

                continue;
            }

            if (!byHash.TryAdd(job.Hash, job))
            {
                ambiguous.Add(job.Hash);
            }
        }

        var state = new StalledJobState
        {
            Version = previous.Version,
            LastRunAt = previous.LastRunAt,
            LastSnapshotAt = now,
            BackoffUntil = previous.BackoffUntil,
            ConsecutiveFailures = previous.ConsecutiveFailures
        };

        var eligible = new List<StallDecision>();
        Int32 candidates = 0, matched = 0, missing = 0, ambiguousCount = 0, qualifying = 0;

        foreach (var torrent in localTorrents)
        {
            if (!IsCandidate(torrent))
            {
                continue;
            }

            candidates++;

            var rdId = torrent.RdId!;

            if (ambiguous.Contains(rdId))
            {
                ambiguousCount++;

                continue;
            }

            // Match the local NZB RdId against the provider *hash*, never the numeric TorBox id.
            if (!byHash.TryGetValue(rdId, out var job))
            {
                missing++;

                continue;
            }

            matched++;

            if (!IsStalledCondition(job))
            {
                continue;
            }

            qualifying++;

            var key = torrent.TorrentId.ToString();
            var fingerprint = Fingerprint(job);

            StalledJobObservation observation;

            if (previousObservations.TryGetValue(key, out var prior)
                && prior.RdId == rdId
                && prior.Fingerprint == fingerprint
                && prior.FirstObservedAt <= now
                && prior.LastObservedAt <= now + ClockSkewTolerance)
            {
                observation = new()
                {
                    RdId = rdId,
                    Fingerprint = fingerprint,
                    FirstObservedAt = prior.FirstObservedAt,
                    LastObservedAt = now,
                    ObservationCount = prior.ObservationCount + 1,
                    ProviderUpdatedAt = job.UpdatedAt
                };
            }
            else
            {
                // New, changed or untrusted history: the stall clock (re)starts now.
                observation = new()
                {
                    RdId = rdId,
                    Fingerprint = fingerprint,
                    FirstObservedAt = now,
                    LastObservedAt = now,
                    ObservationCount = 1,
                    ProviderUpdatedAt = job.UpdatedAt
                };
            }

            state.Observations[key] = observation;

            if (continuityReset != null || observation.ObservationCount < 2)
            {
                continue;
            }

            var observedSpan = observation.LastObservedAt - observation.FirstObservedAt;

            String? reason = null;

            if (observedSpan >= threshold)
            {
                reason = $"observed unchanged by the watchdog for {observedSpan.TotalHours:0.0}h "
                         + $"({observation.ObservationCount} hourly checks since {observation.FirstObservedAt:u})";
            }
            else if (observedSpan >= RetroactiveConfirmationSpan
                     && IsTrustworthyProviderTimestamp(job, now)
                     && now - job.UpdatedAt!.Value >= threshold + RetroactiveProviderAgeMargin)
            {
                reason = $"TorBox updated_at unchanged since {job.UpdatedAt.Value.ToUniversalTime():u} "
                         + $"({(now - job.UpdatedAt.Value).TotalHours:0}h) and confirmed unchanged across "
                         + $"{observation.ObservationCount} watchdog checks since {observation.FirstObservedAt:u}";
            }

            if (reason != null)
            {
                var message = $"Stalled at TorBox: reported processing at 100%, inactive, download_finished=true, "
                              + $"download_present=false; {reason}. Marked failed by stall watchdog. "
                              + $"Provider job and files were not deleted by the watchdog.";

                eligible.Add(new(torrent.TorrentId, rdId, message, observation));
            }
        }

        var toFail = eligible.OrderBy(m => m.Observation.FirstObservedAt).Take(MaxFailuresPerRun).ToList();

        return new(state,
                   toFail,
                   new()
                   {
                       Candidates = candidates,
                       Matched = matched,
                       MissingFromProvider = missing,
                       Ambiguous = ambiguousCount,
                       Qualifying = qualifying,
                       Deferred = eligible.Count - toFail.Count,
                       MalformedProviderEntries = malformed,
                       ContinuityReset = continuityReset
                   });
    }
}
