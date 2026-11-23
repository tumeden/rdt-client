using Microsoft.Extensions.Logging;
using RdtClient.Data.Data;
using RdtClient.Data.Enums;
using RdtClient.Data.Models.Internal;
using RdtClient.Service.Helpers;

namespace RdtClient.Service.Services.StalledJobs;

public enum StalledJobRunOutcome
{
    Disabled,
    Overlapping,
    NotDue,
    BackingOff,
    NoCandidates,
    ProviderError,
    RateLimited,
    Evaluated
}

public sealed record StalledJobRunResult(StalledJobRunOutcome Outcome, IReadOnlyList<Guid> Failed, StalledJobEvaluationStats? Stats = null);

/// <summary>
///     One watchdog run: at most one shared TorBox Usenet snapshot request, evaluation, then narrow per-job
///     "mark failed" mutations through the same data path the built-in lifetime expiry uses.
///     Never deletes provider jobs, torrents or files, never schedules a retry/resubmission.
/// </summary>
public class StalledJobWatchdog(
    ILogger<StalledJobWatchdog> logger,
    ITorrentData torrentData,
    IUsenetSnapshotSource snapshotSource,
    IStalledJobStateStore stateStore,
    IRateLimitCoordinator coordinator,
    ISettings settings,
    ITorrentRunnerState runnerState,
    TimeProvider timeProvider)
{
    public const String TorBoxRateLimitKey = "api.torbox.app";

    private static readonly SemaphoreSlim RunLock = new(1, 1);

    public async Task<StalledJobRunResult> RunOnce(Boolean force = false)
    {
        var threshold = StalledJobEvaluator.GetThreshold(settings.Current.Provider.StalledNzbTimeoutHours);

        if (settings.Current.Provider.Provider != Provider.TorBox || threshold == null || String.IsNullOrWhiteSpace(settings.Current.Provider.ApiKey))
        {
            return new(StalledJobRunOutcome.Disabled, []);
        }

        if (!await RunLock.WaitAsync(0))
        {
            logger.LogDebug("Stall watchdog run skipped: previous run still in progress");

            return new(StalledJobRunOutcome.Overlapping, []);
        }

        try
        {
            return await RunLocked(threshold.Value, force);
        }
        finally
        {
            RunLock.Release();
        }
    }

    private async Task<StalledJobRunResult> RunLocked(TimeSpan threshold, Boolean force)
    {
        var now = timeProvider.GetUtcNow();
        var state = stateStore.Load();

        // Hourly cadence, enforced from persisted state so restarts cannot cause bursts or catch-up runs.
        if (!force && state.LastRunAt is { } lastRun && lastRun <= now && now - lastRun < StalledJobEvaluator.EvaluationInterval - TimeSpan.FromMinutes(1))
        {
            return new(StalledJobRunOutcome.NotDue, []);
        }

        if (state.BackoffUntil is { } backoffUntil && backoffUntil > now)
        {
            logger.LogDebug("Stall watchdog backing off until {BackoffUntil}", backoffUntil);

            return new(StalledJobRunOutcome.BackingOff, []);
        }

        var cooldown = coordinator.GetNextAllowedAt(TorBoxRateLimitKey);

        if (cooldown is { } cooldownUntil && cooldownUntil > now)
        {
            state.BackoffUntil = cooldownUntil;
            stateStore.Save(state);

            return new(StalledJobRunOutcome.BackingOff, []);
        }

        state.LastRunAt = now;

        var localTorrents = await torrentData.Get();

        if (!localTorrents.Any(StalledJobEvaluator.IsCandidate))
        {
            // Nothing submitted and idle at the provider: no provider request is needed at all.
            state.Observations.Clear();
            state.LastSnapshotAt = null;
            state.ConsecutiveFailures = 0;
            state.BackoffUntil = null;
            stateStore.Save(state);

            return new(StalledJobRunOutcome.NoCandidates, []);
        }

        IReadOnlyList<ProviderUsenetJob?>? snapshot;

        try
        {
            snapshot = await snapshotSource.GetSnapshot();
        }
        catch (RateLimitException ex)
        {
            var wait = ex.RetryAfter > StalledJobEvaluator.EvaluationInterval ? ex.RetryAfter : StalledJobEvaluator.EvaluationInterval;
            state.ConsecutiveFailures++;
            state.BackoffUntil = now + wait;
            stateStore.Save(state);

            logger.LogWarning("Stall watchdog: TorBox rate limit (retry after {RetryAfter}); no jobs changed, next attempt after {BackoffUntil}", ex.RetryAfter, state.BackoffUntil);

            return new(StalledJobRunOutcome.RateLimited, []);
        }
        catch (Exception ex)
        {
            RecordProviderFailure(state, now, ex.Message);

            return new(StalledJobRunOutcome.ProviderError, []);
        }

        if (snapshot == null)
        {
            RecordProviderFailure(state, now, "empty/unreadable response");

            return new(StalledJobRunOutcome.ProviderError, []);
        }

        var evaluation = StalledJobEvaluator.Evaluate(now, threshold, localTorrents, snapshot, state);
        var newState = evaluation.State;
        newState.ConsecutiveFailures = 0;
        newState.BackoffUntil = null;

        if (evaluation.Stats.ContinuityReset != null)
        {
            logger.LogInformation("Stall watchdog: observation history reset ({Reason}); no jobs will be failed this run", evaluation.Stats.ContinuityReset);
        }

        if (evaluation.Stats.MalformedProviderEntries > 0 || evaluation.Stats.Ambiguous > 0)
        {
            logger.LogWarning("Stall watchdog: ignored {Malformed} malformed and {Ambiguous} ambiguous TorBox Usenet entries",
                              evaluation.Stats.MalformedProviderEntries,
                              evaluation.Stats.Ambiguous);
        }

        // Persist observations before mutating so a crash mid-run cannot lose stall history.
        stateStore.Save(newState);

        var failed = new List<Guid>();

        foreach (var decision in evaluation.ToFail)
        {
            if (await TryMarkFailed(decision, now))
            {
                failed.Add(decision.TorrentId);
            }

            newState.Observations.Remove(decision.TorrentId.ToString());
        }

        if (evaluation.Stats.Deferred > 0)
        {
            logger.LogWarning("Stall watchdog: {Deferred} further stalled jobs deferred to the next run (per-run cap {Cap})",
                              evaluation.Stats.Deferred,
                              StalledJobEvaluator.MaxFailuresPerRun);
        }

        stateStore.Save(newState);

        return new(StalledJobRunOutcome.Evaluated, failed, evaluation.Stats);
    }

    private void RecordProviderFailure(StalledJobState state, DateTimeOffset now, String message)
    {
        state.ConsecutiveFailures++;

        // 1h, 2h, 4h, 8h, capped at 12h. Observations are kept; continuity rules decide later whether they still count.
        var hours = Math.Min(12, Math.Pow(2, Math.Min(state.ConsecutiveFailures - 1, 4)));
        state.BackoffUntil = now + TimeSpan.FromHours(hours);
        stateStore.Save(state);

        logger.LogWarning("Stall watchdog: TorBox snapshot failed ({Message}); no jobs changed, next attempt after {BackoffUntil}", message, state.BackoffUntil);
    }

    private async Task<Boolean> TryMarkFailed(StallDecision decision, DateTimeOffset now)
    {
        // Re-read local state immediately before mutating (database only, no provider request).
        var current = await torrentData.GetById(decision.TorrentId);

        if (current == null || !StalledJobEvaluator.IsCandidate(current) || current.RdId != decision.RdId)
        {
            logger.LogInformation("Stall watchdog: skipped {TorrentId}, local state changed since evaluation", decision.TorrentId);

            return false;
        }

        if (current.Downloads.Any(m => runnerState.ActiveDownloadClients.ContainsKey(m.DownloadId) || runnerState.ActiveUnpackClients.ContainsKey(m.DownloadId)))
        {
            logger.LogInformation("Stall watchdog: skipped {TorrentId}, local download activity present", decision.TorrentId);

            return false;
        }

        if (current.DeleteOnError > 0)
        {
            // DeleteOnError would later delete the provider job and local files. The watchdog must not cause deletion.
            logger.LogWarning("Stall watchdog: {TorrentName} ({TorrentId}) is stalled at TorBox but has DeleteOnError={DeleteOnError}; not marking failed to avoid deletion",
                              current.RdName,
                              current.TorrentId,
                              current.DeleteOnError);

            return false;
        }

        // Same terminal state as the built-in lifetime expiry (retries exhausted, Completed + Error, no Retry),
        // but written with one conditional UPDATE that re-applies every guard atomically. If a download, retry,
        // DeleteOnError or status change landed after the read above, nothing is written.
        if (!await torrentData.TryMarkStalledFailed(current.TorrentId, decision.RdId, decision.Reason, now))
        {
            logger.LogInformation("Stall watchdog: skipped {TorrentId}, local state changed before the failure write", decision.TorrentId);

            return false;
        }

        logger.LogWarning("Stall watchdog: marked {TorrentName} ({TorrentId}, hash {RdId}) as failed: {Reason}",
                          current.RdName,
                          current.TorrentId,
                          current.RdId,
                          decision.Reason);

        return true;
    }
}
