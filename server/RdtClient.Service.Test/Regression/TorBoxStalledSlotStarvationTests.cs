using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using RdtClient.Data.Data;
using RdtClient.Data.Enums;
using RdtClient.Data.Models.Data;
using RdtClient.Data.Models.Internal;
using RdtClient.Service.Helpers;
using RdtClient.Service.Services;
using RdtClient.Service.Services.DebridClients;
using RdtClient.Service.Services.StalledJobs;
using RdtClient.Service.Test.Services.StalledJobs;
using TorBoxNET;
using Torrent = RdtClient.Data.Models.Data.Torrent;

namespace RdtClient.Service.Test.Regression;

/// <summary>
///     End-to-end regression for the TorBox queue starvation incident, using the real SQLite data layer,
///     the real TorrentRunner scheduler, the real TorBoxDebridClient (only its HTTP client factory method is
///     stubbed so no request can leave the process), the real watchdog and the real SABnzbd compatibility API.
///     ALL DATA IS SYNTHETIC. A dummy non-credential API key is used only to get past TorrentRunner's
///     "no API key" early return.
/// </summary>
[Collection(StalledJobCollection.Name)]
public class TorBoxStalledSlotStarvationTests : IAsyncLifetime
{
    private const String StuckHash = "synthetic0stuck0hash0000000000aa";
    private const String NewHash = "synthetic0new0hash00000000000bbb";

    private readonly String _directory = Path.Combine(Path.GetTempPath(), $"rdt-starvation-{Guid.NewGuid():N}");
    private readonly ManualTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly TestSettings _settings = new();
    private readonly TorrentRunnerState _runnerState = new();
    private readonly Mock<IUsenetApi> _usenetApi = new(MockBehavior.Strict);

    // Raw usenet/mylist payload served by the stubbed TorBoxDebridClient.GetUsenetMyListJson().
    private String _myListJson = """{"success":true,"data":[]}""";
    private Int32 _myListCalls;

    private static String MyListJson(params (String hash, String state, Double progress, Boolean active, Boolean present, Boolean finished, DateTimeOffset updatedAt)[] jobs)
    {
        var data = jobs.Select(j => new Dictionary<String, Object>
        {
            ["id"] = 4242,
            ["hash"] = j.hash,
            ["name"] = $"Synthetic.{j.hash}",
            ["download_state"] = j.state,
            ["progress"] = j.progress,
            ["active"] = j.active,
            ["download_present"] = j.present,
            ["download_finished"] = j.finished,
            ["size"] = 1_000_000,
            ["created_at"] = j.updatedAt.ToString("O"),
            ["updated_at"] = j.updatedAt.ToString("O")
        });

        return System.Text.Json.JsonSerializer.Serialize(new { success = true, error = (String?) null, data });
    }

    private String DatabasePath => Path.Combine(_directory, "rdtclient.db");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);

        _settings.Current.Provider.Provider = Provider.TorBox;
        _settings.Current.Provider.ApiKey = Fixtures.DummyApiKey;
        _settings.Current.Provider.MaxParallelDownloads = 1;
        _settings.Current.Provider.StalledNzbTimeoutHours = 6;
        _settings.Current.DownloadClient.DownloadPath = Path.Combine(_directory, "downloads");

        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, true);

        return Task.CompletedTask;
    }

    private DataContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<DataContext>()
                      .UseSqlite(new SqliteConnectionStringBuilder { DataSource = DatabasePath, ForeignKeys = true }.ToString())
                      .Options;

        return new(options);
    }

    private TorBoxDebridClient CreateTorBoxClient()
    {
        var httpFactory = new Mock<IHttpClientFactory>(MockBehavior.Strict);

        var client = new Mock<TorBoxDebridClient>(Mock.Of<ILogger<TorBoxDebridClient>>(),
                                                  httpFactory.Object,
                                                  Mock.Of<IDownloadableFileFilter>(),
                                                  new RateLimitCoordinator(),
                                                  _settings)
        {
            CallBase = true
        };

        var torBox = new Mock<ITorBoxNetClient>(MockBehavior.Strict);
        torBox.Setup(m => m.Usenet).Returns(_usenetApi.Object);
        client.Protected().Setup<ITorBoxNetClient>("GetClient", ItExpr.IsAny<String>()).Returns(torBox.Object);
        client.Setup(m => m.GetUsenetMyListJson()).ReturnsAsync(() =>
        {
            _myListCalls++;

            return _myListJson;
        });

        return client.Object;
    }

    private (TorrentRunner runner, Torrents torrents, StalledJobWatchdog watchdog, TorrentData data, DataContext context) Create(IStalledJobStateStore store)
    {
        var context = CreateContext();
        var data = new TorrentData(context);
        var torBox = CreateTorBoxClient();

        var torrents = new Torrents(Mock.Of<ILogger<Torrents>>(),
                                    data,
                                    Mock.Of<IDownloads>(),
                                    null!,
                                    null!,
                                    null!,
                                    null!,
                                    null!,
                                    null!,
                                    null!,
                                    torBox,
                                    null!,
                                    _settings,
                                    _runnerState);

        var runner = new TorrentRunner(Mock.Of<ILogger<TorrentRunner>>(),
                                       torrents,
                                       new(null!),
                                       new(null!, torrents),
                                       Mock.Of<IHttpClientFactory>(),
                                       new RateLimitCoordinator(),
                                       _settings,
                                       _runnerState);

        var watchdog = new StalledJobWatchdog(Mock.Of<ILogger<StalledJobWatchdog>>(),
                                              data,
                                              new TorBoxUsenetSnapshotSource(torBox),
                                              store,
                                              new RateLimitCoordinator(),
                                              _settings,
                                              _runnerState,
                                              _clock);

        return (runner, torrents, watchdog, data, context);
    }

    private static UsenetInfoResult ProviderRecord(String hash, String state, Double progress, Boolean active, Boolean present, Boolean finished, DateTimeOffset updatedAt)
    {
        return new()
        {
            Id = 4242,
            Hash = hash,
            Name = $"Synthetic.{hash}",
            DownloadState = state,
            Progress = progress,
            Active = active,
            DownloadPresent = present,
            DownloadFinished = finished,
            Size = 1_000_000,
            CreatedAt = updatedAt,
            UpdatedAt = updatedAt
        };
    }

    private async Task<(Guid stuckId, Guid queuedId)> SeedIncidentShape()
    {
        await using var context = CreateContext();

        var stuck = new Torrent
        {
            TorrentId = Guid.NewGuid(),
            Hash = "local-stuck",
            RdId = StuckHash,
            RdName = "Synthetic.Stuck.S01E01",
            Type = DownloadType.Nzb,
            ClientKind = Provider.TorBox,
            Added = DateTimeOffset.UtcNow.AddDays(-3),
            RdAdded = DateTimeOffset.UtcNow.AddDays(-3),
            RdStatus = TorrentStatus.Downloading,
            RdStatusRaw = "processing",
            RdProgress = 100,
            RdSpeed = 0,
            TorrentRetryAttempts = 2,
            DownloadRetryAttempts = 2,
            HostDownloadAction = TorrentHostDownloadAction.DownloadAll,
            DownloadClient = RdtClient.Data.Enums.DownloadClient.Bezzad,
            Lifetime = 0,
            DeleteOnError = 0
        };

        var queued = new Torrent
        {
            TorrentId = Guid.NewGuid(),
            Hash = "local-queued",
            RdName = "Synthetic.Queued.S01E02",
            Type = DownloadType.Nzb,
            IsFile = false,
            Added = DateTimeOffset.UtcNow.AddDays(-10), // very old local queue entry, never submitted
            RdStatus = TorrentStatus.Queued,
            TorrentRetryAttempts = 2,
            HostDownloadAction = TorrentHostDownloadAction.DownloadAll,
            DownloadClient = RdtClient.Data.Enums.DownloadClient.Bezzad,
            Lifetime = 0,
            Payload = new()
            {
                Content = "https://nzb.invalid/synthetic-queued.nzb"
            }
        };

        context.Torrents.AddRange(stuck, queued);
        await context.SaveChangesAsync();

        return (stuck.TorrentId, queued.TorrentId);
    }

    private async Task<Torrent> Reload(Guid id)
    {
        await using var context = CreateContext();

        return (await new TorrentData(context).GetById(id))!;
    }

    [Fact]
    public async Task WatchdogFailure_ReleasesSlot_AndQueuedJobAdvances_WithoutDeletionOrRetry()
    {
        var (stuckId, queuedId) = await SeedIncidentShape();
        var store = new InMemoryStateStore();
        var (runner, torrents, watchdog, _, context) = Create(store);

        await using var _ = context;

        _myListJson = MyListJson((StuckHash, "processing", 1.0, false, false, true, DateTimeOffset.UtcNow.AddDays(-3)));

        _usenetApi.Setup(m => m.AddLinkAsync("https://nzb.invalid/synthetic-queued.nzb", It.IsAny<Int32>(), It.IsAny<String?>(), It.IsAny<String?>(), It.IsAny<Boolean>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new Response<UsenetAddResult> { Data = new() { Hash = NewHash } });

        _usenetApi.Setup(m => m.GetHashInfoAsync(NewHash, true, It.IsAny<Int32>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(ProviderRecord(NewHash, "downloading", 0.1, true, false, false, DateTimeOffset.UtcNow));

        // 1. Starvation reproduced: the stalled job holds the only slot, the old queued job is not submitted.
        await runner.Tick();
        await runner.Tick();
        _usenetApi.Verify(m => m.AddLinkAsync(It.IsAny<String>(), It.IsAny<Int32>(), It.IsAny<String?>(), It.IsAny<String?>(), It.IsAny<Boolean>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Null((await Reload(queuedId)).RdId);

        // 2. First watchdog run only observes.
        Assert.Empty((await watchdog.RunOnce()).Failed);
        Assert.Null((await Reload(stuckId)).Completed);

        // 3. One hour later the unchanged, days-old provider record is confirmed and the job is failed.
        _clock.Advance(TimeSpan.FromHours(1));
        var result = await watchdog.RunOnce();
        Assert.Equal([stuckId], result.Failed);

        var failed = await Reload(stuckId);
        Assert.NotNull(failed.Completed);
        Assert.StartsWith("Stalled at TorBox", failed.Error);
        Assert.Equal(failed.TorrentRetryAttempts, failed.RetryCount);
        Assert.Null(failed.Retry);
        Assert.Equal(TorrentStatus.Downloading, failed.RdStatus); // still active-looking, like the incident

        // Exactly one shared snapshot request per run, no per-job provider lookups by the watchdog.
        Assert.Equal(2, _myListCalls);
        _usenetApi.Verify(m => m.GetHashInfoAsync(StuckHash, It.IsAny<Boolean>(), It.IsAny<Int32>(), It.IsAny<CancellationToken>()), Times.Never);

        // 4. Real scheduler: the failed job no longer occupies the slot, the queued job is submitted.
        await runner.Tick();

        var advanced = await Reload(queuedId);
        Assert.Equal(NewHash, advanced.RdId);
        _usenetApi.Verify(m => m.AddLinkAsync("https://nzb.invalid/synthetic-queued.nzb", It.IsAny<Int32>(), It.IsAny<String?>(), It.IsAny<String?>(), It.IsAny<Boolean>(), It.IsAny<CancellationToken>()),
                          Times.Once);

        // 5. No retry / resubmission loop and no deletion over further ticks.
        await runner.Tick();
        await runner.Tick();

        var afterTicks = await Reload(stuckId);
        Assert.Equal(failed.RetryCount, afterTicks.RetryCount);
        Assert.Null(afterTicks.Retry);
        Assert.Equal(StuckHash, afterTicks.RdId);
        _usenetApi.Verify(m => m.ControlAsync(It.IsAny<String>(), It.IsAny<String>(), It.IsAny<Boolean>(), It.IsAny<CancellationToken>()), Times.Never);
        _usenetApi.Verify(m => m.AddLinkAsync(It.IsAny<String>(), It.IsAny<Int32>(), It.IsAny<String?>(), It.IsAny<String?>(), It.IsAny<Boolean>(), It.IsAny<CancellationToken>()), Times.Once);

        // 6. Failure is visible through the SABnzbd compatibility API (what Sonarr/Seekarr consume).
        var sab = new Sabnzbd(Mock.Of<ILogger<Sabnzbd>>(), torrents, new AppSettings(), _settings);
        var history = await sab.GetHistory();
        var slot = Assert.Single(history.Slots, s => s.NzoId == "local-stuck");
        Assert.Equal("Failed", slot.Status);

        var queue = await sab.GetQueue();
        Assert.DoesNotContain(queue.Slots, s => s.NzoId == "local-stuck");
    }

    [Fact]
    public async Task GenuinelyActiveJob_KeepsItsSlot_AndIsNotFailed()
    {
        var (stuckId, queuedId) = await SeedIncidentShape();
        var store = new InMemoryStateStore();
        var (runner, _, watchdog, _, context) = Create(store);

        await using var _ = context;

        // Provider says the job is actively working (not the stalled shape).
        _myListJson = MyListJson((StuckHash, "processing", 1.0, true, false, true, DateTimeOffset.UtcNow.AddDays(-3)));

        for (var i = 0; i < 12; i++)
        {
            await watchdog.RunOnce();
            _clock.Advance(TimeSpan.FromHours(1));
        }

        await runner.Tick();

        Assert.Null((await Reload(stuckId)).Completed);
        Assert.Null((await Reload(queuedId)).RdId);
        _usenetApi.Verify(m => m.AddLinkAsync(It.IsAny<String>(), It.IsAny<Int32>(), It.IsAny<String?>(), It.IsAny<String?>(), It.IsAny<Boolean>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task BuiltInLifetimeExpiry_TerminalJobWithActiveLookingStatus_NoLongerBlocksQueue()
    {
        // Reproduces the isolated container experiment: Lifetime expiry sets Completed+Error but leaves RdStatus=3.
        var (stuckId, queuedId) = await SeedIncidentShape();

        await using (var context = CreateContext())
        {
            var stuck = await context.Torrents.SingleAsync(m => m.TorrentId == stuckId);
            stuck.Lifetime = 1;
            await context.SaveChangesAsync();
        }

        var (runner, _, _, _, ctx) = Create(new InMemoryStateStore());

        await using var _ = ctx;

        _usenetApi.Setup(m => m.AddLinkAsync(It.IsAny<String>(), It.IsAny<Int32>(), It.IsAny<String?>(), It.IsAny<String?>(), It.IsAny<Boolean>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new Response<UsenetAddResult> { Data = new() { Hash = NewHash } });

        _usenetApi.Setup(m => m.GetHashInfoAsync(NewHash, true, It.IsAny<Int32>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(ProviderRecord(NewHash, "downloading", 0.1, true, false, false, DateTimeOffset.UtcNow));

        await runner.Tick(); // lifetime expiry happens here (the old queued job has Lifetime=0 and is untouched)
        await runner.Tick(); // next tick sees the terminal job and dequeues

        var expired = await Reload(stuckId);
        Assert.NotNull(expired.Completed);
        Assert.Equal("Torrent lifetime of 1 minutes reached", expired.Error);
        Assert.Equal(TorrentStatus.Downloading, expired.RdStatus);
        Assert.Equal(expired.TorrentRetryAttempts, expired.RetryCount);

        var queued = await Reload(queuedId);
        Assert.Equal(NewHash, queued.RdId);
        Assert.Null(queued.Error);
    }

    [Fact]
    public async Task ZeroParallelLimit_DequeuesEverything_RegardlessOfStalledJobs()
    {
        var (_, queuedId) = await SeedIncidentShape();
        _settings.Current.Provider.MaxParallelDownloads = 0;

        var (runner, _, _, _, ctx) = Create(new InMemoryStateStore());

        await using var _ = ctx;

        _usenetApi.Setup(m => m.AddLinkAsync(It.IsAny<String>(), It.IsAny<Int32>(), It.IsAny<String?>(), It.IsAny<String?>(), It.IsAny<Boolean>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new Response<UsenetAddResult> { Data = new() { Hash = NewHash } });

        _usenetApi.Setup(m => m.GetHashInfoAsync(NewHash, true, It.IsAny<Int32>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(ProviderRecord(NewHash, "downloading", 0.1, true, false, false, DateTimeOffset.UtcNow));

        await runner.Tick();

        Assert.Equal(NewHash, (await Reload(queuedId)).RdId);
    }
}

[CollectionDefinition(Name)]
public class StalledJobCollection
{
    public const String Name = "StalledJobWatchdog";
}
