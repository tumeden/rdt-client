using RdtClient.Data.Enums;
using RdtClient.Data.Models.Data;
using RdtClient.Service.Services.StalledJobs;
using static RdtClient.Service.Test.Services.StalledJobs.Fixtures;

namespace RdtClient.Service.Test.Services.StalledJobs;

public class StalledJobEvaluatorTest
{
    private static readonly TimeSpan Threshold = TimeSpan.FromHours(6);

    /// <summary>Runs the evaluator hourly for <paramref name="hours" /> runs, returning the hour (index) of the first failure.</summary>
    private static (Int32? failedAtRun, StalledJobState state) RunHourly(IList<Torrent> local,
                                                                       Func<Int32, IEnumerable<ProviderUsenetJob?>> snapshotAt,
                                                                       Int32 runs,
                                                                       StalledJobState? state = null)
    {
        state ??= new();

        for (var i = 0; i < runs; i++)
        {
            var result = StalledJobEvaluator.Evaluate(T0.AddHours(i), Threshold, local, snapshotAt(i), state);
            state = result.State;

            if (result.ToFail.Count > 0)
            {
                return (i, state);
            }
        }

        return (null, state);
    }

    [Fact]
    public void OldUnsubmittedQueueJob_IsNeverACandidate_EvenAfterDays()
    {
        var queued = UnsubmittedQueued("backlog", T0.AddDays(-60));
        var halfSubmitted = SubmittedNzb("half");
        halfSubmitted.RdAdded = null;

        Assert.False(StalledJobEvaluator.IsCandidate(queued));
        Assert.False(StalledJobEvaluator.IsCandidate(halfSubmitted));

        // Even if the provider snapshot somehow contained a matching hash, nothing is failed.
        var (failedAt, state) = RunHourly([queued, halfSubmitted], _ => [StalledJob("local-backlog", T0.AddDays(-60)), StalledJob("half", T0.AddDays(-60))], 48);

        Assert.Null(failedAt);
        Assert.Empty(state.Observations);
    }

    [Fact]
    public void UnchangedQualifyingJob_WithoutProviderTimestamp_FailsAfterSixObservedHours()
    {
        var job = SubmittedNzb("stuck");

        var (failedAt, _) = RunHourly([job], _ => [StalledJob("stuck", updatedAt: null)], 24);

        Assert.Equal(6, failedAt); // first observation at hour 0, 6h observed at hour 6
    }

    [Fact]
    public void ProgressingJob_ResetsTimer()
    {
        var job = SubmittedNzb("moving");

        // Provider state changes at hour 3 (e.g. updated_at bumps / progress changes), then freezes.
        var (failedAt, _) = RunHourly([job],
                                      i => [StalledJob("moving", updatedAt: i < 3 ? T0.AddHours(-1) : T0.AddHours(2.5))],
                                      24);

        Assert.Equal(9, failedAt); // 6h after the change observed at hour 3
    }

    [Fact]
    public void NewProcessingJob_IsNotFailed_OnFirstObservation_EvenWithOldTimestamp()
    {
        var job = SubmittedNzb("fresh");

        var result = StalledJobEvaluator.Evaluate(T0, Threshold, [job], [StalledJob("fresh", T0.AddDays(-5))], new());

        Assert.Empty(result.ToFail);
        Assert.Equal(1, result.State.Observations.Single().Value.ObservationCount);
    }

    [Fact]
    public void RecentlyUpdatedJob_IsNotRetroactive_AndWaitsForSixObservedHours()
    {
        var job = SubmittedNzb("recent");

        // updated_at 10h old: below the 24h retroactive bar, so only our own observations count.
        var (failedAt, _) = RunHourly([job], _ => [StalledJob("recent", T0.AddHours(-10))], 24);

        Assert.Equal(6, failedAt);
    }

    [Fact]
    public void Retroactive_AlreadyStuckForDays_FailsOnSecondHourlyConfirmation()
    {
        var job = SubmittedNzb("days-old");

        var (failedAt, _) = RunHourly([job], _ => [StalledJob("days-old", T0.AddDays(-3))], 24);

        Assert.Equal(1, failedAt);
    }

    [Fact]
    public void Retroactive_RequiresAtLeast55MinutesOfConfirmation()
    {
        var job = SubmittedNzb("quick");
        var state = StalledJobEvaluator.Evaluate(T0, Threshold, [job], [StalledJob("quick", T0.AddDays(-3))], new()).State;

        var tooSoon = StalledJobEvaluator.Evaluate(T0.AddMinutes(30), Threshold, [job], [StalledJob("quick", T0.AddDays(-3))], state);

        Assert.Empty(tooSoon.ToFail);
    }

    [Fact]
    public void Retroactive_FutureOrMissingTimestamp_IsNotTrusted()
    {
        var future = StalledJob("x", T0.AddHours(2));
        var missing = StalledJob("x", null);
        var beforeCreated = StalledJob("x", T0.AddDays(-10)) with
        {
            CreatedAt = T0.AddDays(-3)
        };

        Assert.False(StalledJobEvaluator.IsTrustworthyProviderTimestamp(future, T0));
        Assert.False(StalledJobEvaluator.IsTrustworthyProviderTimestamp(missing, T0));
        Assert.False(StalledJobEvaluator.IsTrustworthyProviderTimestamp(beforeCreated, T0));
        Assert.True(StalledJobEvaluator.IsTrustworthyProviderTimestamp(StalledJob("x", T0.AddDays(-2)), T0));
    }

    [Fact]
    public void Retroactive_DoesNotUseLocalAddedOrProviderCreatedAt()
    {
        // Local Added and provider CreatedAt are ancient, updated_at is unknown: must wait for 6 observed hours.
        var job = SubmittedNzb("created-old", T0.AddDays(-30));
        var provider = StalledJob("created-old", null) with
        {
            CreatedAt = T0.AddDays(-30)
        };

        var (failedAt, _) = RunHourly([job], _ => [provider], 24);

        Assert.Equal(6, failedAt);
    }

    public static TheoryData<String, Func<ProviderUsenetJob, ProviderUsenetJob>> NonQualifyingProviderShapes => new()
    {
        { "download present", j => j with { DownloadPresent = true } },
        { "active", j => j with { Active = true } },
        { "not finished", j => j with { DownloadFinished = false } },
        { "progress 99%", j => j with { Progress = 0.99 } },
        { "progress NaN", j => j with { Progress = Double.NaN } },
        { "progress missing", j => j with { Progress = null } },
        { "state completed", j => j with { DownloadState = "completed" } },
        { "state downloading", j => j with { DownloadState = "downloading" } },
        { "state missing", j => j with { DownloadState = null } },
        { "active missing", j => j with { Active = null } },
        { "present missing", j => j with { DownloadPresent = null } }
    };

    [Theory]
    [MemberData(nameof(NonQualifyingProviderShapes))]
    public void NonQualifyingProviderState_IsNeverFailed(String label, Func<ProviderUsenetJob, ProviderUsenetJob> mutate)
    {
        var job = SubmittedNzb("nq");

        var (failedAt, state) = RunHourly([job], _ => [mutate(StalledJob("nq", T0.AddDays(-3)))], 24);

        Assert.True(failedAt == null, label);
        Assert.Empty(state.Observations);
    }

    public static TheoryData<String, Action<Torrent>> ExcludedLocalStates => new()
    {
        { "completed", t => t.Completed = T0.AddDays(-1) },
        { "errored", t => t.Error = "previous error" },
        { "retry scheduled", t => t.Retry = T0 },
        { "has local downloads", t => t.Downloads = [new() { DownloadId = Guid.NewGuid(), Link = "https://example.invalid/f" }] },
        { "provider finished", t => t.RdStatus = TorrentStatus.Finished },
        { "provider error", t => t.RdStatus = TorrentStatus.Error },
        { "torrent type", t => t.Type = DownloadType.Torrent },
        { "other provider", t => t.ClientKind = Provider.RealDebrid }
    };

    [Theory]
    [MemberData(nameof(ExcludedLocalStates))]
    public void ExcludedLocalState_IsNeverFailed(String label, Action<Torrent> mutate)
    {
        var job = SubmittedNzb("ex");
        mutate(job);

        var (failedAt, _) = RunHourly([job], _ => [StalledJob("ex", T0.AddDays(-3))], 24);

        Assert.True(failedAt == null, label);
    }

    [Fact]
    public void MissingProviderRecord_DropsObservation_AndRestartsWhenItReappears()
    {
        var job = SubmittedNzb("flaky");

        // Present hours 0-4, missing hour 5, present again from hour 6 onwards.
        var (failedAt, _) = RunHourly([job], i => i == 5 ? [] : [StalledJob("flaky")], 24);

        Assert.Equal(12, failedAt);
    }

    [Fact]
    public void EmptySnapshot_FailsNothing()
    {
        var jobs = Enumerable.Range(0, 20).Select(i => SubmittedNzb($"h{i}")).ToList();

        var (failedAt, state) = RunHourly(jobs, _ => [], 48);

        Assert.Null(failedAt);
        Assert.Empty(state.Observations);
    }

    [Fact]
    public void MalformedAndDuplicateEntries_AreIgnored()
    {
        var dup = SubmittedNzb("dup");
        var ok = SubmittedNzb("ok");

        var snapshot = new List<ProviderUsenetJob?>
        {
            null,
            new() { Hash = "" },
            new() { Hash = null, DownloadState = "processing" },
            StalledJob("dup", T0.AddDays(-3)),
            StalledJob("DUP", T0.AddDays(-3)),
            StalledJob("ok", T0.AddDays(-3))
        };

        var first = StalledJobEvaluator.Evaluate(T0, Threshold, [dup, ok], snapshot, new());
        var second = StalledJobEvaluator.Evaluate(T0.AddHours(1), Threshold, [dup, ok], snapshot, first.State);

        Assert.Equal(3, second.Stats.MalformedProviderEntries);
        Assert.Equal(1, second.Stats.Ambiguous);
        Assert.Equal([ok.TorrentId], second.ToFail.Select(m => m.TorrentId));
    }

    [Fact]
    public void MatchesByHash_CaseInsensitive_NotNumericTorBoxId()
    {
        var byHash = SubmittedNzb("ABCDEF0123");
        var numeric = SubmittedNzb("987654"); // a numeric TorBox id stored where the hash belongs

        var snapshot = new List<ProviderUsenetJob?>
        {
            StalledJob("abcdef0123", T0.AddDays(-3)),
            StalledJob("someotherhash", T0.AddDays(-3))
        };

        var first = StalledJobEvaluator.Evaluate(T0, Threshold, [byHash, numeric], snapshot, new());
        var second = StalledJobEvaluator.Evaluate(T0.AddHours(1), Threshold, [byHash, numeric], snapshot, first.State);

        Assert.Equal([byHash.TorrentId], second.ToFail.Select(m => m.TorrentId));
        Assert.Equal(1, second.Stats.MissingFromProvider);
    }

    [Fact]
    public void ClockMovingBackwards_ResetsHistory_AndFailsNothing()
    {
        var job = SubmittedNzb("clock");
        var (_, state) = RunHourly([job], _ => [StalledJob("clock")], 5);

        var result = StalledJobEvaluator.Evaluate(T0.AddHours(-2), Threshold, [job], [StalledJob("clock")], state);

        Assert.NotNull(result.Stats.ContinuityReset);
        Assert.Empty(result.ToFail);
        Assert.Equal(1, result.State.Observations.Single().Value.ObservationCount);
    }

    [Fact]
    public void LongGapBetweenSnapshots_ResetsHistory_NoMassFailure()
    {
        var jobs = Enumerable.Range(0, 5).Select(i => SubmittedNzb($"gap{i}")).ToList();
        var snapshot = jobs.Select(j => (ProviderUsenetJob?) StalledJob(j.RdId!)).ToList();

        var (_, state) = RunHourly(jobs, _ => snapshot, 5); // 4h observed so far

        // Watchdog/container was down for 10h; the stall may have been fine in between, we cannot know.
        var afterGap = StalledJobEvaluator.Evaluate(T0.AddHours(14), Threshold, jobs, snapshot, state);

        Assert.NotNull(afterGap.Stats.ContinuityReset);
        Assert.Empty(afterGap.ToFail);
    }

    [Fact]
    public void PerRunCap_DefersExcessJobs()
    {
        var jobs = Enumerable.Range(0, 15).Select(i => SubmittedNzb($"cap{i}")).ToList();
        var snapshot = jobs.Select(j => (ProviderUsenetJob?) StalledJob(j.RdId!, T0.AddDays(-3))).ToList();

        var first = StalledJobEvaluator.Evaluate(T0, Threshold, jobs, snapshot, new());
        var second = StalledJobEvaluator.Evaluate(T0.AddHours(1), Threshold, jobs, snapshot, first.State);

        Assert.Equal(StalledJobEvaluator.MaxFailuresPerRun, second.ToFail.Count);
        Assert.Equal(5, second.Stats.Deferred);
    }

    [Fact]
    public void FailureReason_IsDescriptive_AndSaysNothingWasDeleted()
    {
        var job = SubmittedNzb("reason");
        var (_, state) = RunHourly([job], _ => [StalledJob("reason", T0.AddDays(-3))], 1);

        var result = StalledJobEvaluator.Evaluate(T0.AddHours(1), Threshold, [job], [StalledJob("reason", T0.AddDays(-3))], state);

        var reason = Assert.Single(result.ToFail).Reason;
        Assert.StartsWith("Stalled at TorBox", reason);
        Assert.Contains("not deleted", reason);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(-1, null)]
    [InlineData(1, 6)]
    [InlineData(6, 6)]
    [InlineData(12, 12)]
    public void Threshold_ClampedToSixHours_ZeroDisables(Int32 configured, Int32? expectedHours)
    {
        Assert.Equal(expectedHours, StalledJobEvaluator.GetThreshold(configured)?.TotalHours is { } h ? (Int32) h : null);
    }

    [Fact]
    public void Evaluate_NeverAcceptsThresholdBelowMinimum()
    {
        var job = SubmittedNzb("min");
        var state = new StalledJobState();

        for (var i = 0; i < 6; i++)
        {
            var result = StalledJobEvaluator.Evaluate(T0.AddHours(i), TimeSpan.FromMinutes(1), [job], [StalledJob("min")], state);
            Assert.Empty(result.ToFail);
            state = result.State;
        }
    }
}
