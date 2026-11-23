using Microsoft.Extensions.Logging;
using Moq;
using RdtClient.Data.Data;
using RdtClient.Data.Enums;
using RdtClient.Data.Models.Data;
using RdtClient.Data.Models.Internal;
using RdtClient.Service.Helpers;
using RdtClient.Service.Services;
using RdtClient.Service.Services.StalledJobs;
using static RdtClient.Service.Test.Services.StalledJobs.Fixtures;

namespace RdtClient.Service.Test.Services.StalledJobs;

[Collection(RdtClient.Service.Test.Regression.StalledJobCollection.Name)]
public class StalledJobWatchdogTest
{
    private readonly ManualTimeProvider _clock = new(T0);
    private readonly RateLimitCoordinator _coordinator = new();
    private readonly List<Torrent> _local = [];
    private readonly TorrentRunnerState _runnerState = new();
    private readonly TestSettings _settings = new();
    private readonly Mock<IUsenetSnapshotSource> _snapshot = new(MockBehavior.Strict);
    private readonly InMemoryStateStore _store = new();
    private readonly Mock<ITorrentData> _torrentData = new(MockBehavior.Strict);

    public StalledJobWatchdogTest()
    {
        _settings.Current.Provider.Provider = Provider.TorBox;
        _settings.Current.Provider.ApiKey = DummyApiKey;
        _settings.Current.Provider.StalledNzbTimeoutHours = 6;

        _torrentData.Setup(m => m.Get()).ReturnsAsync(() => _local.ToList());
        _torrentData.Setup(m => m.GetById(It.IsAny<Guid>())).ReturnsAsync((Guid id) => _local.FirstOrDefault(m => m.TorrentId == id));

        // In-memory stand-in for the conditional UPDATE (the real SQL guards are tested in TorrentDataStalledFailTests).
        _torrentData.Setup(m => m.TryMarkStalledFailed(It.IsAny<Guid>(), It.IsAny<String>(), It.IsAny<String>(), It.IsAny<DateTimeOffset>()))
                    .ReturnsAsync((Guid id, String rdId, String error, DateTimeOffset at) =>
                    {
                        var t = _local.First(m => m.TorrentId == id);

                        if (t.RdId != rdId || t.Completed != null || t.Error != null || t.Downloads.Count > 0 || t.DeleteOnError > 0)
                        {
                            return false;
                        }

                        t.Error = error;
                        t.Completed = at;
                        t.RetryCount = t.TorrentRetryAttempts;

                        return true;
                    });
    }

    private StalledJobWatchdog CreateWatchdog(IStalledJobStateStore? store = null)
    {
        return new(Mock.Of<ILogger<StalledJobWatchdog>>(),
                   _torrentData.Object,
                   _snapshot.Object,
                   store ?? _store,
                   _coordinator,
                   _settings,
                   _runnerState,
                   _clock);
    }

    private void SnapshotReturns(params ProviderUsenetJob?[] jobs)
    {
        _snapshot.Setup(m => m.GetSnapshot()).ReturnsAsync(jobs);
    }

    private void VerifyNoDeletionOrResubmission()
    {
        _torrentData.Verify(m => m.Delete(It.IsAny<Guid>()), Times.Never);
        _torrentData.Verify(m => m.UpdateRetry(It.IsAny<Guid>(), It.IsAny<DateTimeOffset?>(), It.IsAny<Int32>()), Times.Never);
        _torrentData.Verify(m => m.UpdateComplete(It.IsAny<Guid>(), It.IsAny<String?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<Boolean>()), Times.Never);
    }

    [Fact]
    public async Task ConditionalWriteRejected_IsNotReportedAsFailed()
    {
        var job = SubmittedNzb("cas");
        _local.Add(job);
        SnapshotReturns(StalledJob("cas", T0.AddDays(-3)));
        var watchdog = CreateWatchdog();
        await watchdog.RunOnce();

        // GetById still shows the old idle state, but the atomic UPDATE sees a change and writes nothing.
        _torrentData.Setup(m => m.TryMarkStalledFailed(job.TorrentId, It.IsAny<String>(), It.IsAny<String>(), It.IsAny<DateTimeOffset>())).ReturnsAsync(false);

        _clock.Advance(TimeSpan.FromHours(1));

        Assert.Empty((await watchdog.RunOnce()).Failed);
        Assert.Null(job.Completed);
    }

    [Fact]
    public async Task StuckJob_IsMarkedFailed_WithRetriesExhausted_AndNothingDeleted()
    {
        var stuck = SubmittedNzb("stuck");
        _local.Add(stuck);
        SnapshotReturns(StalledJob("stuck", T0.AddDays(-3)));

        var watchdog = CreateWatchdog();

        Assert.Equal(StalledJobRunOutcome.Evaluated, (await watchdog.RunOnce()).Outcome);
        Assert.Null(stuck.Completed);

        _clock.Advance(TimeSpan.FromHours(1));
        var result = await watchdog.RunOnce();

        Assert.Equal([stuck.TorrentId], result.Failed);
        Assert.NotNull(stuck.Completed);
        Assert.StartsWith("Stalled at TorBox", stuck.Error);
        Assert.Equal(stuck.TorrentRetryAttempts, stuck.RetryCount);
        Assert.Null(stuck.Retry);
        Assert.Equal(TorrentStatus.Downloading, stuck.RdStatus); // untouched; slot accounting handles it
        VerifyNoDeletionOrResubmission();
        Assert.Empty(_store.State.Observations);
    }

    [Fact]
    public async Task HourlyCadence_SecondRunWithinTheHour_MakesNoProviderRequest()
    {
        _local.Add(SubmittedNzb("a"));
        SnapshotReturns(StalledJob("a"));
        var watchdog = CreateWatchdog();

        await watchdog.RunOnce();

        for (var i = 0; i < 11; i++)
        {
            _clock.Advance(TimeSpan.FromMinutes(5));
            Assert.Equal(StalledJobRunOutcome.NotDue, (await watchdog.RunOnce()).Outcome);
        }

        _snapshot.Verify(m => m.GetSnapshot(), Times.Once);
    }

    [Fact]
    public async Task AfterLongDowntime_RunsOnce_NoCatchUpBurst()
    {
        _local.Add(SubmittedNzb("a"));
        SnapshotReturns(StalledJob("a"));
        var watchdog = CreateWatchdog();
        await watchdog.RunOnce();

        _clock.Advance(TimeSpan.FromHours(10));
        await watchdog.RunOnce();
        await watchdog.RunOnce();
        await watchdog.RunOnce();

        _snapshot.Verify(m => m.GetSnapshot(), Times.Exactly(2));
    }

    [Fact]
    public async Task ProviderError_PreservesJobs_AndBacksOffExponentially()
    {
        var job = SubmittedNzb("err");
        _local.Add(job);
        _snapshot.Setup(m => m.GetSnapshot()).ThrowsAsync(new HttpRequestException("connection refused"));
        var watchdog = CreateWatchdog();

        Assert.Equal(StalledJobRunOutcome.ProviderError, (await watchdog.RunOnce()).Outcome);
        Assert.Equal(T0.AddHours(1), _store.State.BackoffUntil);

        _clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(StalledJobRunOutcome.ProviderError, (await watchdog.RunOnce()).Outcome);
        Assert.Equal(_clock.Now.AddHours(2), _store.State.BackoffUntil);

        _clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(StalledJobRunOutcome.BackingOff, (await watchdog.RunOnce()).Outcome);

        _snapshot.Verify(m => m.GetSnapshot(), Times.Exactly(2));
        Assert.Null(job.Completed);
        VerifyNoDeletionOrResubmission();
    }

    [Fact]
    public async Task NullSnapshot_IsTreatedAsFailure()
    {
        var job = SubmittedNzb("null");
        _local.Add(job);
        _snapshot.Setup(m => m.GetSnapshot()).ReturnsAsync((IReadOnlyList<ProviderUsenetJob?>?) null);

        Assert.Equal(StalledJobRunOutcome.ProviderError, (await CreateWatchdog().RunOnce()).Outcome);
        Assert.Null(job.Completed);
    }

    [Theory]
    [InlineData(2, 60)] // short Retry-After never shortens the hourly cadence
    [InlineData(180, 180)] // long Retry-After is honored
    public async Task RateLimit_HonorsRetryAfter(Int32 retryAfterMinutes, Int32 expectedBackoffMinutes)
    {
        _local.Add(SubmittedNzb("rl"));
        _snapshot.Setup(m => m.GetSnapshot()).ThrowsAsync(new RateLimitException("slow_down", TimeSpan.FromMinutes(retryAfterMinutes)));

        var result = await CreateWatchdog().RunOnce();

        Assert.Equal(StalledJobRunOutcome.RateLimited, result.Outcome);
        Assert.Equal(T0.AddMinutes(expectedBackoffMinutes), _store.State.BackoffUntil);
    }

    [Fact]
    public async Task SharedCoordinatorCooldown_SkipsProviderRequest()
    {
        _local.Add(SubmittedNzb("cd"));
        _coordinator.UpdateCooldown(StalledJobWatchdog.TorBoxRateLimitKey, TimeSpan.FromHours(2));

        // The coordinator uses the real clock; any active cooldown must block the run.
        _clock.Now = DateTimeOffset.UtcNow;

        Assert.Equal(StalledJobRunOutcome.BackingOff, (await CreateWatchdog().RunOnce()).Outcome);
        _snapshot.Verify(m => m.GetSnapshot(), Times.Never);
    }

    [Fact]
    public async Task NoCandidates_MakesNoProviderRequest()
    {
        _local.Add(UnsubmittedQueued("q1"));
        _local.Add(UnsubmittedQueued("q2"));

        Assert.Equal(StalledJobRunOutcome.NoCandidates, (await CreateWatchdog().RunOnce()).Outcome);
        _snapshot.Verify(m => m.GetSnapshot(), Times.Never);
    }

    [Theory]
    [InlineData(Provider.RealDebrid, 6, DummyApiKey)]
    [InlineData(Provider.AllDebrid, 6, DummyApiKey)]
    [InlineData(Provider.TorBox, 0, DummyApiKey)]
    [InlineData(Provider.TorBox, 6, "")]
    public async Task Disabled_ForOtherProviders_ZeroTimeout_OrNoKey(Provider provider, Int32 hours, String apiKey)
    {
        _settings.Current.Provider.Provider = provider;
        _settings.Current.Provider.StalledNzbTimeoutHours = hours;
        _settings.Current.Provider.ApiKey = apiKey;
        _local.Add(SubmittedNzb("d"));

        Assert.Equal(StalledJobRunOutcome.Disabled, (await CreateWatchdog().RunOnce()).Outcome);
        _snapshot.Verify(m => m.GetSnapshot(), Times.Never);
        _torrentData.Verify(m => m.Get(), Times.Never);
    }

    [Fact]
    public async Task OldUnsubmittedQueue_IsUntouched_WhileSubmittedStuckJobFails()
    {
        var backlog = Enumerable.Range(0, 122).Select(i => UnsubmittedQueued($"backlog{i}", T0.AddDays(-20))).ToList();
        var stuck = SubmittedNzb("stuck");
        _local.AddRange(backlog);
        _local.Add(stuck);
        SnapshotReturns(StalledJob("stuck", T0.AddDays(-3)));
        var watchdog = CreateWatchdog();

        for (var i = 0; i < 12; i++)
        {
            await watchdog.RunOnce();
            _clock.Advance(TimeSpan.FromHours(1));
        }

        Assert.NotNull(stuck.Completed);
        Assert.All(backlog, t =>
        {
            Assert.Null(t.Completed);
            Assert.Null(t.Error);
        });

        foreach (var t in backlog)
        {
            _torrentData.Verify(m => m.TryMarkStalledFailed(t.TorrentId, It.IsAny<String>(), It.IsAny<String>(), It.IsAny<DateTimeOffset>()), Times.Never);
        }
    }

    [Fact]
    public async Task StaleObservation_LocalStateChangedBeforeMutation_IsSkipped()
    {
        var job = SubmittedNzb("race");
        _local.Add(job);
        SnapshotReturns(StalledJob("race", T0.AddDays(-3)));
        var watchdog = CreateWatchdog();
        await watchdog.RunOnce();

        // Between evaluation (Get) and mutation (GetById) a local download starts.
        var raced = SubmittedNzb("race");
        raced.TorrentId = job.TorrentId;
        raced.Downloads = [new() { DownloadId = Guid.NewGuid(), TorrentId = job.TorrentId }];
        _torrentData.Setup(m => m.GetById(job.TorrentId)).ReturnsAsync(raced);

        _clock.Advance(TimeSpan.FromHours(1));
        var result = await watchdog.RunOnce();

        Assert.Empty(result.Failed);
        Assert.Null(job.Completed);
        _torrentData.Verify(m => m.TryMarkStalledFailed(It.IsAny<Guid>(), It.IsAny<String>(), It.IsAny<String>(), It.IsAny<DateTimeOffset>()), Times.Never);
    }

    [Fact]
    public async Task ProgressingAgain_BeforeMutation_IsSkippedByFreshEvaluation()
    {
        var job = SubmittedNzb("revive");
        _local.Add(job);
        SnapshotReturns(StalledJob("revive", T0.AddDays(-3)));
        var watchdog = CreateWatchdog();
        await watchdog.RunOnce();

        SnapshotReturns(StalledJob("revive", T0.AddDays(-3)) with { Active = true });
        _clock.Advance(TimeSpan.FromHours(1));

        Assert.Empty((await watchdog.RunOnce()).Failed);
        Assert.Null(job.Completed);
    }

    [Fact]
    public async Task DeleteOnError_Job_IsNotFailed_ToAvoidDeletion()
    {
        var job = SubmittedNzb("doe");
        job.DeleteOnError = 30;
        _local.Add(job);
        SnapshotReturns(StalledJob("doe", T0.AddDays(-3)));
        var watchdog = CreateWatchdog();

        await watchdog.RunOnce();
        _clock.Advance(TimeSpan.FromHours(1));

        Assert.Empty((await watchdog.RunOnce()).Failed);
        Assert.Null(job.Completed);
    }

    [Fact]
    public async Task AttemptTimestamp_IsPersistedBeforeProviderRequest_CrashCannotCauseEarlyRecheck()
    {
        _local.Add(SubmittedNzb("crash"));

        // Snapshot of the on-disk state at the moment the request is in flight (i.e. what a crash would leave).
        var diskAtCrash = new InMemoryStateStore();
        DateTimeOffset? persistedAtRequest = null;

        _snapshot.Setup(m => m.GetSnapshot())
                 .Returns(() =>
                 {
                     persistedAtRequest = _store.State.LastRunAt;
                     diskAtCrash.Save(_store.Load());

                     throw new TaskCanceledException("process killed mid-request");
                 });

        await CreateWatchdog().RunOnce();

        Assert.Equal(T0, persistedAtRequest);

        // "Restarted" process reading only what was on disk at the crash, 30 minutes later.
        _clock.Advance(TimeSpan.FromMinutes(30));

        Assert.Equal(StalledJobRunOutcome.NotDue, (await CreateWatchdog(diskAtCrash).RunOnce()).Outcome);
        _snapshot.Verify(m => m.GetSnapshot(), Times.Once);
    }

    [Fact]
    public async Task OverlappingRuns_SecondRunIsRejected()
    {
        _local.Add(SubmittedNzb("ov"));
        var gate = new TaskCompletionSource<IReadOnlyList<ProviderUsenetJob?>?>();
        _snapshot.Setup(m => m.GetSnapshot()).Returns(gate.Task);

        var first = CreateWatchdog().RunOnce();
        var second = await CreateWatchdog().RunOnce(force: true);

        Assert.Equal(StalledJobRunOutcome.Overlapping, second.Outcome);

        gate.SetResult([StalledJob("ov")]);
        Assert.Equal(StalledJobRunOutcome.Evaluated, (await first).Outcome);
        _snapshot.Verify(m => m.GetSnapshot(), Times.Once);
    }

    [Fact]
    public async Task RestartWithPersistedFileState_ContinuesObservation()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rdt-stall-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            var appSettings = new AppSettings { Database = new() { Path = Path.Combine(dir, "rdtclient.db") } };
            var job = SubmittedNzb("persist");
            _local.Add(job);
            SnapshotReturns(StalledJob("persist", null));

            // Five hourly runs, each by a "new process" with a fresh store instance reading the same file.
            for (var i = 0; i < 6; i++)
            {
                var store = new StalledJobStateStore(Mock.Of<ILogger<StalledJobStateStore>>(), appSettings);
                await CreateWatchdog(store).RunOnce();
                Assert.Null(job.Completed);
                _clock.Advance(TimeSpan.FromHours(1));
            }

            Assert.True(File.Exists(Path.Combine(dir, StalledJobStateStore.FileName)));

            var finalStore = new StalledJobStateStore(Mock.Of<ILogger<StalledJobStateStore>>(), appSettings);
            Assert.Equal(6, finalStore.Load().Observations.Single().Value.ObservationCount);

            var result = await CreateWatchdog(finalStore).RunOnce();
            Assert.Equal([job.TorrentId], result.Failed);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void CorruptStateFile_LoadsAsEmpty()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rdt-stall-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            var appSettings = new AppSettings { Database = new() { Path = Path.Combine(dir, "rdtclient.db") } };
            File.WriteAllText(Path.Combine(dir, StalledJobStateStore.FileName), "{ not json");

            var state = new StalledJobStateStore(Mock.Of<ILogger<StalledJobStateStore>>(), appSettings).Load();

            Assert.Empty(state.Observations);
            Assert.Null(state.LastRunAt);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
