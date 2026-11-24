using System.Collections.Concurrent;

namespace RdtClient.Service.Services;

/// <summary>
///     Pacing of the automatic removal of failed jobs. In memory only: what still has to be removed is simply what is
///     still in the list, so a restart loses nothing.
/// </summary>
public class FailedJobRemovalState
{
    private readonly ConcurrentDictionary<Guid, (Int32 Failures, DateTimeOffset Until)> _postponed = new();

    /// <summary>Wait between two removals (at most two provider requests each).</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>First wait after a failure. Doubles per failure of the same job, up to <see cref="MaxRetryDelay" />.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Nothing is removed before this time.</summary>
    public DateTimeOffset NextAllowedAt { get; set; } = DateTimeOffset.MinValue;

    public Boolean IsAllowed(Guid torrentId, DateTimeOffset now)
    {
        return !_postponed.TryGetValue(torrentId, out var postponed) || postponed.Until <= now;
    }

    public DateTimeOffset Fail(Guid torrentId, DateTimeOffset now)
    {
        var failures = (_postponed.TryGetValue(torrentId, out var postponed) ? postponed.Failures : 0) + 1;
        var delay = RetryDelay * Math.Pow(2, Math.Min(failures - 1, 10));
        var until = now + (delay < MaxRetryDelay ? delay : MaxRetryDelay);

        _postponed[torrentId] = (failures, until);

        return until;
    }

    public void Postpone(Guid torrentId, DateTimeOffset now)
    {
        var failures = _postponed.TryGetValue(torrentId, out var postponed) ? postponed.Failures : 0;
        _postponed[torrentId] = (failures, now + RetryDelay);
    }

    public void Forget(Guid torrentId)
    {
        _postponed.TryRemove(torrentId, out _);
    }

    public void Clear()
    {
        NextAllowedAt = DateTimeOffset.MinValue;
        _postponed.Clear();
    }
}
