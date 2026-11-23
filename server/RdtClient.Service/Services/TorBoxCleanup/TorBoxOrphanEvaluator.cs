using RdtClient.Data.Models.Data;

namespace RdtClient.Service.Services.TorBoxCleanup;

public sealed record OrphanEvaluation(OrphanCleanupState State, IReadOnlyList<OrphanObservation> Eligible, Int32 Orphans, Int32 Deferred, Int32 Malformed, String? Skipped);

/// <summary>Pure decision logic for TorBox leftover cleanup. Fails closed.</summary>
public static class TorBoxOrphanEvaluator
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    public static readonly TimeSpan MinimumAge = TimeSpan.FromHours(6);
    public static readonly TimeSpan MaxContinuityGap = TimeSpan.FromHours(3);
    public const Int32 MaxActionsPerRun = 10;

    public static TimeSpan GetMinAge(Int32 hours)
    {
        var age = TimeSpan.FromHours(Math.Max(hours, 0));

        return age < MinimumAge ? MinimumAge : age;
    }

    /// <summary>Every identifier rdt-client knows for its own jobs (provider hash, provider id, local hash).</summary>
    public static HashSet<String> KnownIds(IEnumerable<Torrent> local)
    {
        var known = new HashSet<String>(StringComparer.OrdinalIgnoreCase);

        foreach (var t in local)
        {
            if (!String.IsNullOrWhiteSpace(t.RdId))
            {
                known.Add(t.RdId.Trim());
            }

            if (!String.IsNullOrWhiteSpace(t.Hash))
            {
                known.Add(t.Hash.Trim());
            }
        }

        return known;
    }

    public static Boolean IsKnown(TorBoxJob job, HashSet<String> known)
    {
        return (!String.IsNullOrWhiteSpace(job.Hash) && known.Contains(job.Hash.Trim()))
               || (job.Id != null && known.Contains(job.Id.Value.ToString()));
    }

    public static OrphanEvaluation Evaluate(DateTimeOffset now, TimeSpan minAge, IList<Torrent> local, IEnumerable<TorBoxJob?> snapshot, OrphanCleanupState previous)
    {
        if (minAge < MinimumAge)
        {
            minAge = MinimumAge;
        }

        var state = new OrphanCleanupState
        {
            LastRunAt = previous.LastRunAt,
            LastSnapshotAt = now,
            BackoffUntil = previous.BackoffUntil,
            ConsecutiveFailures = previous.ConsecutiveFailures
        };

        // An empty local database (fresh install, wiped/restored DB) would make every TorBox job look like a leftover.
        if (local.Count == 0)
        {
            return new(state, [], 0, 0, 0, "rdt-client has no jobs at all; refusing to treat the whole TorBox account as leftovers");
        }

        var continuityBroken = previous.LastSnapshotAt is { } last && (now < last || now - last > MaxContinuityGap);
        var prior = continuityBroken ? new(StringComparer.OrdinalIgnoreCase) : previous.Observations;

        var known = KnownIds(local);
        var malformed = 0;
        var orphans = 0;
        var eligible = new List<OrphanObservation>();
        // A record needs both a numeric id (to delete) and a hash (NZBs are tracked locally by hash), otherwise it
        // could be a tracked job we simply fail to recognise.
        var valid = new List<TorBoxJob>();

        foreach (var job in snapshot)
        {
            if (job?.Id == null || String.IsNullOrWhiteSpace(job.Hash))
            {
                malformed++;

                continue;
            }

            valid.Add(job);
        }

        // Any id or hash that appears more than once is ambiguous (conflicting duplicates): never act on it.
        var ambiguousKeys = valid.GroupBy(m => m.Key, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ambiguousHashes = valid.GroupBy(m => m.Hash!.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var job in valid)
        {
            if (ambiguousKeys.Contains(job.Key) || ambiguousHashes.Contains(job.Hash!.Trim()) || IsKnown(job, known) || job.Active != false)
            {
                continue;
            }

            orphans++;

            var observation = prior.TryGetValue(job.Key, out var p) && p.FirstSeenAt <= now && String.Equals(p.Hash, job.Hash, StringComparison.OrdinalIgnoreCase)
                ? new OrphanObservation
                {
                    Type = job.Type, Id = job.Id.Value, Hash = job.Hash, Name = job.Name,
                    FirstSeenAt = p.FirstSeenAt, LastSeenAt = now, Count = p.Count + 1, LastReportedAt = p.LastReportedAt
                }
                : new OrphanObservation
                {
                    Type = job.Type, Id = job.Id.Value, Hash = job.Hash, Name = job.Name,
                    FirstSeenAt = now, LastSeenAt = now, Count = 1
                };

            state.Observations[job.Key] = observation;

            if (observation.Count >= 2 && now - observation.FirstSeenAt >= minAge)
            {
                eligible.Add(observation);
            }
        }

        var take = eligible.OrderBy(m => m.FirstSeenAt).Take(MaxActionsPerRun).ToList();

        return new(state, take, orphans, eligible.Count - take.Count, malformed, continuityBroken ? "observation history reset (clock change or long gap)" : null);
    }
}
