using System.Text.Json;
using Microsoft.Extensions.Logging;
using Moq;
using RdtClient.Data.Data;
using RdtClient.Data.Enums;
using RdtClient.Data.Models.Data;
using RdtClient.Data.Models.Internal;
using RdtClient.Service.Helpers;
using RdtClient.Service.Services;
using RdtClient.Service.Services.DebridClients;
using RdtClient.Service.Services.TorBoxCleanup;
using RdtClient.Service.Test.Services.StalledJobs;

namespace RdtClient.Service.Test.Services.TorBoxCleanup;

// SYNTHETIC DATA ONLY. The TorBox client is mocked; no request can leave the process.
[Collection(Regression.StalledJobCollection.Name)]
public class TorBoxOrphanCleanupTest
{
    private static readonly DateTimeOffset T0 = Fixtures.T0;

    private readonly ManualTimeProvider _clock = new(T0);
    private readonly List<Torrent> _local = [];
    private readonly TestSettings _settings = new();
    private readonly InMemoryOrphanStore _store = new();
    private readonly Mock<TorBoxDebridClient> _torBox;
    private readonly Mock<ITorrentData> _torrentData = new(MockBehavior.Strict);

    private List<Object> _torrentItems = [];
    private List<Object> _usenetItems = [];

    private sealed class InMemoryOrphanStore : IOrphanCleanupStateStore
    {
        public OrphanCleanupState State { get; private set; } = new();

        public OrphanCleanupState Load()
        {
            return JsonSerializer.Deserialize<OrphanCleanupState>(JsonSerializer.Serialize(State))!;
        }

        public void Save(OrphanCleanupState state)
        {
            State = JsonSerializer.Deserialize<OrphanCleanupState>(JsonSerializer.Serialize(state))!;
        }
    }

    public TorBoxOrphanCleanupTest()
    {
        _settings.Current.Provider.Provider = Provider.TorBox;
        _settings.Current.Provider.ApiKey = Fixtures.DummyApiKey;
        _settings.Current.Provider.TorBoxOrphanCleanup = TorBoxOrphanCleanupMode.Delete;
        _settings.Current.Provider.TorBoxOrphanMinAgeHours = 24;

        _torrentData.Setup(m => m.Get()).ReturnsAsync(() => _local.ToList());

        _torBox = new(Mock.Of<ILogger<TorBoxDebridClient>>(), Mock.Of<IHttpClientFactory>(), Mock.Of<IDownloadableFileFilter>(), new RateLimitCoordinator(), _settings);
        _torBox.Setup(m => m.GetTorrentMyListJson()).ReturnsAsync(() => Envelope(_torrentItems));
        _torBox.Setup(m => m.GetUsenetMyListJson()).ReturnsAsync(() => Envelope(_usenetItems));
        _torBox.Setup(m => m.DeleteProviderJob(It.IsAny<DownloadType>(), It.IsAny<Int64>())).Returns(Task.CompletedTask);

        // A local job so the "empty database" guard does not apply.
        _local.Add(new() { TorrentId = Guid.NewGuid(), Hash = "local-known", RdId = "known-usenet-hash", Type = DownloadType.Nzb });
    }

    private static String Envelope(List<Object> items)
    {
        return JsonSerializer.Serialize(new { success = true, data = items });
    }

    private static Object Item(Int64 id, String hash, Boolean active = false, String name = "Synthetic.Leftover")
    {
        return new Dictionary<String, Object> { ["id"] = id, ["hash"] = hash, ["name"] = name, ["active"] = active, ["download_state"] = "completed" };
    }

    private TorBoxOrphanCleanup Create()
    {
        return new(Mock.Of<ILogger<TorBoxOrphanCleanup>>(), _torrentData.Object, _torBox.Object, _store, new RateLimitCoordinator(), _settings, _clock);
    }

    private async Task RunHourly(Int32 runs)
    {
        for (var i = 0; i < runs; i++)
        {
            await Create().RunOnce();
            _clock.Advance(TimeSpan.FromHours(1));
        }
    }

    private void VerifyDeleted(DownloadType type, Int64 id, Times times)
    {
        _torBox.Verify(m => m.DeleteProviderJob(type, id), times);
    }

    [Fact]
    public async Task InactiveLeftover_IsRemovedOnlyAfterMinAge()
    {
        _usenetItems = [Item(11, "leftover-hash")];

        await RunHourly(24); // hours 0..23: seen, but younger than 24h
        VerifyDeleted(DownloadType.Nzb, 11, Times.Never());

        await RunHourly(1); // hour 24
        VerifyDeleted(DownloadType.Nzb, 11, Times.Once());
    }

    [Fact]
    public async Task JobsRdtClientKnows_AreNeverTouched()
    {
        _local.Add(new() { Hash = "local-torrent-hash", RdId = "501", Type = DownloadType.Torrent });
        _local.Add(new() { Hash = "legacy-torrent-hash", RdId = "legacy-torrent-hash", Type = DownloadType.Torrent });

        _usenetItems = [Item(1, "KNOWN-USENET-HASH")];
        _torrentItems = [Item(501, "x"), Item(502, "legacy-torrent-hash"), Item(503, "local-torrent-hash")];

        await RunHourly(30);

        _torBox.Verify(m => m.DeleteProviderJob(It.IsAny<DownloadType>(), It.IsAny<Int64>()), Times.Never);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public async Task ActiveOrUnknownActivity_IsNeverRemoved(Boolean? active)
    {
        var item = new Dictionary<String, Object> { ["id"] = 12L, ["hash"] = "busy" };

        if (active != null)
        {
            item["active"] = active.Value;
        }

        _usenetItems = [item];

        await RunHourly(30);

        VerifyDeleted(DownloadType.Nzb, 12, Times.Never());
    }

    [Fact]
    public async Task BecomingActiveOrKnown_ResetsTheClock()
    {
        _usenetItems = [Item(13, "flip")];
        await RunHourly(20);

        _usenetItems = [Item(13, "flip", active: true)];
        await RunHourly(1);

        _usenetItems = [Item(13, "flip")];
        await RunHourly(24);
        VerifyDeleted(DownloadType.Nzb, 13, Times.Never());

        await RunHourly(1);
        VerifyDeleted(DownloadType.Nzb, 13, Times.Once());
    }

    [Fact]
    public async Task ReportOnly_NeverDeletes()
    {
        _settings.Current.Provider.TorBoxOrphanCleanup = TorBoxOrphanCleanupMode.ReportOnly;
        _torrentItems = [Item(21, "leftover-t")];

        var reported = 0;

        for (var i = 0; i < 30; i++)
        {
            reported += (await Create().RunOnce()).Reported.Count;
            _clock.Advance(TimeSpan.FromHours(1));
        }

        Assert.True(reported > 0);
        _torBox.Verify(m => m.DeleteProviderJob(It.IsAny<DownloadType>(), It.IsAny<Int64>()), Times.Never);
    }

    [Theory]
    [InlineData(TorBoxOrphanCleanupMode.Off, Provider.TorBox, false)]
    [InlineData(TorBoxOrphanCleanupMode.Delete, Provider.RealDebrid, false)]
    [InlineData(TorBoxOrphanCleanupMode.Delete, Provider.TorBox, true)] // AutoImport on
    public async Task Disabled_MakesNoRequests(TorBoxOrphanCleanupMode mode, Provider provider, Boolean autoImport)
    {
        _settings.Current.Provider.TorBoxOrphanCleanup = mode;
        _settings.Current.Provider.Provider = provider;
        _settings.Current.Provider.AutoImport = autoImport;

        Assert.Equal(OrphanCleanupOutcome.Disabled, (await Create().RunOnce()).Outcome);
        _torBox.Verify(m => m.GetTorrentMyListJson(), Times.Never);
        _torBox.Verify(m => m.GetUsenetMyListJson(), Times.Never);
    }

    [Fact]
    public void DefaultSetting_IsOff()
    {
        Assert.Equal(TorBoxOrphanCleanupMode.Off, new DbSettings().Provider.TorBoxOrphanCleanup);
    }

    [Fact]
    public async Task EmptyLocalDatabase_RemovesNothing()
    {
        _local.Clear();
        _usenetItems = Enumerable.Range(1, 50).Select(i => Item(i, $"h{i}")).ToList();

        await RunHourly(48);

        _torBox.Verify(m => m.DeleteProviderJob(It.IsAny<DownloadType>(), It.IsAny<Int64>()), Times.Never);
    }

    [Fact]
    public async Task AtMostTenRemovalsPerRun()
    {
        _usenetItems = Enumerable.Range(100, 25).Select(i => Item(i, $"bulk{i}")).ToList();

        await RunHourly(25);

        _torBox.Verify(m => m.DeleteProviderJob(DownloadType.Nzb, It.IsAny<Int64>()), Times.Exactly(TorBoxOrphanEvaluator.MaxActionsPerRun));
    }

    [Fact]
    public async Task JobAddedLocallyBeforeDelete_IsSpared()
    {
        _usenetItems = [Item(31, "late-known")];
        await RunHourly(24);

        // rdt-client starts tracking it (e.g. user re-added) right before the deciding run.
        var calls = 0;
        _torrentData.Setup(m => m.Get()).ReturnsAsync(() =>
        {
            calls++;

            return calls >= 2 ? [.._local, new() { Hash = "x", RdId = "late-known", Type = DownloadType.Nzb }] : _local.ToList();
        });

        await RunHourly(1);

        VerifyDeleted(DownloadType.Nzb, 31, Times.Never());
    }

    [Fact]
    public async Task UnreadableResponse_BacksOff_NoDeletes()
    {
        _torBox.Setup(m => m.GetUsenetMyListJson()).ReturnsAsync("""{"success":false,"error":"AUTH"}""");
        _usenetItems = [Item(41, "x")];

        Assert.Equal(OrphanCleanupOutcome.ProviderError, (await Create().RunOnce()).Outcome);
        Assert.Equal(T0.AddHours(1), _store.State.BackoffUntil);
        _torBox.Verify(m => m.DeleteProviderJob(It.IsAny<DownloadType>(), It.IsAny<Int64>()), Times.Never);
    }

    [Fact]
    public async Task RateLimitDuringDelete_StopsAndBacksOff()
    {
        _usenetItems = Enumerable.Range(200, 5).Select(i => Item(i, $"rl{i}")).ToList();
        _torBox.Setup(m => m.DeleteProviderJob(It.IsAny<DownloadType>(), It.IsAny<Int64>())).ThrowsAsync(new RateLimitException("slow down", TimeSpan.FromHours(2)));

        await RunHourly(24);
        await Create().RunOnce();

        _torBox.Verify(m => m.DeleteProviderJob(It.IsAny<DownloadType>(), It.IsAny<Int64>()), Times.Once);
        Assert.Equal(_clock.Now.AddHours(2), _store.State.BackoffUntil);
    }

    [Fact]
    public async Task HourlyCadence_AndAttemptPersistedBeforeRequest()
    {
        DateTimeOffset? persisted = null;
        _torBox.Setup(m => m.GetTorrentMyListJson()).ReturnsAsync(() =>
        {
            persisted = _store.State.LastRunAt;

            return Envelope([]);
        });

        await Create().RunOnce();
        _clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(OrphanCleanupOutcome.NotDue, (await Create().RunOnce()).Outcome);

        Assert.Equal(T0, persisted);
        _torBox.Verify(m => m.GetTorrentMyListJson(), Times.Once);
    }

    [Fact]
    public async Task LongGap_ResetsObservations()
    {
        _usenetItems = [Item(51, "gap")];
        await RunHourly(20);

        _clock.Advance(TimeSpan.FromHours(10)); // app was down
        await RunHourly(23);

        VerifyDeleted(DownloadType.Nzb, 51, Times.Never());
    }

    [Theory]
    [InlineData(0, 6)]
    [InlineData(1, 6)]
    [InlineData(24, 24)]
    public void MinAge_FloorIsSixHours(Int32 configured, Int32 expected)
    {
        Assert.Equal(TimeSpan.FromHours(expected), TorBoxOrphanEvaluator.GetMinAge(configured));
    }

    [Fact]
    public void Evaluator_KnownJobs_AreNotEvenObserved()
    {
        var local = new List<Torrent> { new() { Hash = "lh", RdId = "KNOWN", Type = DownloadType.Nzb }, new() { Hash = "th", RdId = "77", Type = DownloadType.Torrent } };

        var snapshot = new List<TorBoxJob?>
        {
            new() { Type = DownloadType.Nzb, Id = 1, Hash = "known", Active = false },
            new() { Type = DownloadType.Torrent, Id = 77, Hash = "z", Active = false },
            new() { Type = DownloadType.Nzb, Id = 2, Hash = "orphan", Active = false }
        };

        var result = TorBoxOrphanEvaluator.Evaluate(T0, TimeSpan.FromHours(24), local, snapshot, new());

        Assert.Equal(["Nzb:2"], result.State.Observations.Keys);
    }

    [Fact]
    public async Task RecordWithoutHash_IsNeverRemoved()
    {
        // A tracked NZB is matched by hash; without one we cannot prove it is not ours.
        _usenetItems = [new Dictionary<String, Object> { ["id"] = 61L, ["active"] = false, ["name"] = "no-hash" }];

        await RunHourly(48);

        VerifyDeleted(DownloadType.Nzb, 61, Times.Never());
    }

    [Fact]
    public async Task ConflictingDuplicates_AreNeverRemoved()
    {
        // Same id twice (one active), and same hash under two ids.
        _usenetItems = [Item(71, "dup-a"), Item(71, "dup-a", active: true), Item(72, "dup-b"), Item(73, "dup-b")];

        await RunHourly(48);

        _torBox.Verify(m => m.DeleteProviderJob(It.IsAny<DownloadType>(), It.IsAny<Int64>()), Times.Never);
    }

    [Fact]
    public async Task OwnershipIsRecheckedPerItem_JobTrackedAfterEarlierDeleteIsSpared()
    {
        _usenetItems = [Item(81, "first"), Item(82, "second")];
        await RunHourly(24);

        // After the first delete, rdt-client starts tracking the second one.
        _torBox.Setup(m => m.DeleteProviderJob(DownloadType.Nzb, 81))
               .Callback(() => _local.Add(new() { Hash = "x2", RdId = "second", Type = DownloadType.Nzb }))
               .Returns(Task.CompletedTask);

        await RunHourly(1);

        VerifyDeleted(DownloadType.Nzb, 81, Times.Once());
        VerifyDeleted(DownloadType.Nzb, 82, Times.Never());
    }

    [Fact]
    public async Task Delete_WaitsForTheSubmissionLock()
    {
        _usenetItems = [Item(91, "locked")];
        await RunHourly(24);

        await Torrents.ProviderMutationLock.WaitAsync();
        Task<OrphanCleanupResult> run;

        try
        {
            run = Create().RunOnce();
            await Task.Delay(200);

            VerifyDeleted(DownloadType.Nzb, 91, Times.Never());

            // While the lock is held, rdt-client "submits" the same job and records its hash.
            _local.Add(new() { Hash = "x3", RdId = "locked", Type = DownloadType.Nzb });
        }
        finally
        {
            Torrents.ProviderMutationLock.Release();
        }

        await run;

        VerifyDeleted(DownloadType.Nzb, 91, Times.Never());
    }
    [Fact]
    public void Parser_MalformedItemsIgnored_BadEnvelopeNull()
    {
        var parsed = TorBoxMyListParser.Parse("""{"success":true,"data":[{"hash":"noid"},{"id":"7"},1,{"id":5,"active":false}]}""", DownloadType.Nzb)!;

        Assert.Equal(4, parsed.Count);
        Assert.Single(parsed, m => m != null);
        Assert.Null(TorBoxMyListParser.Parse("""{"success":true,"data":{}}""", DownloadType.Nzb));
        Assert.Null(TorBoxMyListParser.Parse("nope", DownloadType.Nzb));
    }
}
