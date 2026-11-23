using System.Text.Json;
using Microsoft.Extensions.Logging;
using RdtClient.Data.Data;
using RdtClient.Data.Enums;
using RdtClient.Data.Models.Internal;
using RdtClient.Service.Helpers;
using RdtClient.Service.Services.DebridClients;

namespace RdtClient.Service.Services.TorBoxCleanup;

public interface IOrphanCleanupStateStore
{
    OrphanCleanupState Load();
    void Save(OrphanCleanupState state);
}

/// <summary>JSON next to the database (/data/db/torbox-orphan-cleanup.json). Atomic writes; unreadable = empty state.</summary>
public class OrphanCleanupStateStore(ILogger<OrphanCleanupStateStore> logger, AppSettings appSettings) : IOrphanCleanupStateStore
{
    public const String FileName = "torbox-orphan-cleanup.json";
    private static readonly Object FileLock = new();
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private String FilePath
    {
        get
        {
            var db = appSettings.Database?.Path;
            var dir = String.IsNullOrWhiteSpace(db) ? null : Path.GetDirectoryName(Path.GetFullPath(db));

            return Path.Combine(dir ?? AppContext.BaseDirectory, FileName);
        }
    }

    public OrphanCleanupState Load()
    {
        lock (FileLock)
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    return new();
                }

                var state = JsonSerializer.Deserialize<OrphanCleanupState>(File.ReadAllText(FilePath), Options) ?? new();
                state.Observations = new(state.Observations ?? new(), StringComparer.OrdinalIgnoreCase);

                return state;
            }
            catch (Exception ex)
            {
                logger.LogWarning("TorBox cleanup state could not be read ({Message}); starting empty", ex.Message);

                return new();
            }
        }
    }

    public void Save(OrphanCleanupState state)
    {
        lock (FileLock)
        {
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state, Options));
            File.Move(temp, FilePath, true);
        }
    }
}

public enum OrphanCleanupOutcome
{
    Disabled,
    Overlapping,
    NotDue,
    BackingOff,
    ProviderError,
    RateLimited,
    Skipped,
    Evaluated
}

public sealed record OrphanCleanupResult(OrphanCleanupOutcome Outcome, IReadOnlyList<String> Reported, IReadOnlyList<String> Removed);

/// <summary>
///     Hourly TorBox leftover cleanup: two raw mylist requests (torrents + usenet), then at most
///     <see cref="TorBoxOrphanEvaluator.MaxActionsPerRun" /> report/delete actions. Single attempt per request,
///     honours Retry-After and shared cooldowns, backs off on errors. Never touches local files or rdt-client jobs.
/// </summary>
public class TorBoxOrphanCleanup(
    ILogger<TorBoxOrphanCleanup> logger,
    ITorrentData torrentData,
    TorBoxDebridClient torBox,
    IOrphanCleanupStateStore store,
    IRateLimitCoordinator coordinator,
    ISettings settings,
    TimeProvider clock)
{
    private static readonly SemaphoreSlim RunLock = new(1, 1);

    public async Task<OrphanCleanupResult> RunOnce()
    {
        var provider = settings.Current.Provider;

        if (provider.Provider != Provider.TorBox || provider.TorBoxOrphanCleanup == TorBoxOrphanCleanupMode.Off || String.IsNullOrWhiteSpace(provider.ApiKey) || provider.AutoImport)
        {
            return new(OrphanCleanupOutcome.Disabled, [], []);
        }

        if (!await RunLock.WaitAsync(0))
        {
            return new(OrphanCleanupOutcome.Overlapping, [], []);
        }

        try
        {
            return await RunLocked(provider.TorBoxOrphanCleanup, TorBoxOrphanEvaluator.GetMinAge(provider.TorBoxOrphanMinAgeHours));
        }
        finally
        {
            RunLock.Release();
        }
    }

    private async Task<OrphanCleanupResult> RunLocked(TorBoxOrphanCleanupMode mode, TimeSpan minAge)
    {
        var now = clock.GetUtcNow();
        var state = store.Load();

        if (state.LastRunAt is { } last && last <= now && now - last < TorBoxOrphanEvaluator.Interval - TimeSpan.FromMinutes(1))
        {
            return new(OrphanCleanupOutcome.NotDue, [], []);
        }

        if (state.BackoffUntil > now)
        {
            return new(OrphanCleanupOutcome.BackingOff, [], []);
        }

        if (coordinator.GetNextAllowedAt("api.torbox.app") is { } cooldown && cooldown > now)
        {
            state.BackoffUntil = cooldown;
            store.Save(state);

            return new(OrphanCleanupOutcome.BackingOff, [], []);
        }

        // Persist the attempt before any request so a crash cannot cause an early re-run.
        state.LastRunAt = now;
        store.Save(state);

        List<TorBoxJob?> snapshot;

        try
        {
            var torrentJobs = TorBoxMyListParser.Parse(await torBox.GetTorrentMyListJson(), DownloadType.Torrent);
            var usenetJobs = torrentJobs == null ? null : TorBoxMyListParser.Parse(await torBox.GetUsenetMyListJson(), DownloadType.Nzb);

            if (torrentJobs == null || usenetJobs == null)
            {
                return Fail(state, now, "unreadable mylist response");
            }

            snapshot = [..torrentJobs, ..usenetJobs];
        }
        catch (RateLimitException ex)
        {
            state.ConsecutiveFailures++;
            state.BackoffUntil = now + (ex.RetryAfter > TorBoxOrphanEvaluator.Interval ? ex.RetryAfter : TorBoxOrphanEvaluator.Interval);
            store.Save(state);
            logger.LogWarning("TorBox cleanup: rate limited, next attempt after {BackoffUntil}", state.BackoffUntil);

            return new(OrphanCleanupOutcome.RateLimited, [], []);
        }
        catch (Exception ex)
        {
            return Fail(state, now, ex.Message);
        }

        // Read local jobs after the snapshot so anything submitted meanwhile is known.
        var local = await torrentData.Get();
        var evaluation = TorBoxOrphanEvaluator.Evaluate(now, minAge, local, snapshot, state);
        var newState = evaluation.State;
        newState.ConsecutiveFailures = 0;
        newState.BackoffUntil = null;
        store.Save(newState);

        if (evaluation.Skipped != null && local.Count == 0)
        {
            logger.LogWarning("TorBox cleanup skipped: {Reason}", evaluation.Skipped);

            return new(OrphanCleanupOutcome.Skipped, [], []);
        }

        var reported = new List<String>();
        var removed = new List<String>();

        if (evaluation.Eligible.Count > 0)
        {
            foreach (var item in evaluation.Eligible)
            {
                var job = new TorBoxJob { Type = item.Type, Id = item.Id, Hash = item.Hash };
                var label = $"{item.Type} \"{item.Name}\" (TorBox id {item.Id}, inactive leftover since {item.FirstSeenAt:u})";

                if (mode == TorBoxOrphanCleanupMode.ReportOnly)
                {
                    if (TorBoxOrphanEvaluator.IsKnown(job, TorBoxOrphanEvaluator.KnownIds(await torrentData.Get())))
                    {
                        newState.Observations.Remove(job.Key);

                        continue;
                    }

                    if (item.LastReportedAt == null || now - item.LastReportedAt >= TimeSpan.FromHours(24))
                    {
                        logger.LogWarning("TorBox cleanup (report only): would remove {Item}", label);
                        newState.Observations[job.Key].LastReportedAt = now;
                    }

                    reported.Add(job.Key);

                    continue;
                }

                try
                {
                    // Hold the same lock rdt-client uses for submission (add + RdId write) and provider sync, and
                    // re-read local ownership for this item inside it, so a job cannot become tracked between the
                    // check and the delete.
                    await Torrents.ProviderMutationLock.WaitAsync();

                    try
                    {
                        if (TorBoxOrphanEvaluator.IsKnown(job, TorBoxOrphanEvaluator.KnownIds(await torrentData.Get())))
                        {
                            newState.Observations.Remove(job.Key);

                            continue;
                        }

                        await torBox.DeleteProviderJob(item.Type, item.Id);
                    }
                    finally
                    {
                        Torrents.ProviderMutationLock.Release();
                    }

                    newState.Observations.Remove(job.Key);
                    removed.Add(job.Key);
                    logger.LogWarning("TorBox cleanup: removed leftover {Item}", label);
                }
                catch (RateLimitException ex)
                {
                    newState.BackoffUntil = now + (ex.RetryAfter > TorBoxOrphanEvaluator.Interval ? ex.RetryAfter : TorBoxOrphanEvaluator.Interval);
                    logger.LogWarning("TorBox cleanup: rate limited while removing, stopping until {BackoffUntil}", newState.BackoffUntil);

                    break;
                }
                catch (Exception ex)
                {
                    logger.LogWarning("TorBox cleanup: could not remove {Item}: {Message}; stopping this run", label, ex.Message);

                    break;
                }
            }
        }

        store.Save(newState);

        return new(OrphanCleanupOutcome.Evaluated, reported, removed);
    }

    private OrphanCleanupResult Fail(OrphanCleanupState state, DateTimeOffset now, String message)
    {
        state.ConsecutiveFailures++;
        state.BackoffUntil = now + TimeSpan.FromHours(Math.Min(12, Math.Pow(2, Math.Min(state.ConsecutiveFailures - 1, 4))));
        store.Save(state);
        logger.LogWarning("TorBox cleanup: snapshot failed ({Message}); nothing changed, next attempt after {BackoffUntil}", message, state.BackoffUntil);

        return new(OrphanCleanupOutcome.ProviderError, [], []);
    }
}
