using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using RdtClient.Data.Data;
using RdtClient.Data.Enums;
using RdtClient.Data.Models.Data;
using RdtClient.Data.Models.DebridClient;
using RdtClient.Data.Models.Internal;
using RdtClient.Service.Helpers;
using RdtClient.Service.Services;
using RdtClient.Service.Services.DebridClients;
using RdtClient.Service.Test.Helpers;
using TorBoxNET;
using Torrent = RdtClient.Data.Models.Data.Torrent;

namespace RdtClient.Service.Test.Regression;

/// <summary>
///     Executable payload blocking through the real SQLite data layer, real Torrents/TorrentRunner, the real
///     TorBoxDebridClient (strict mocked API, no network) and the real SABnzbd API. SYNTHETIC DATA ONLY.
/// </summary>
public class ExecutableBlockingTests : IAsyncLifetime
{
    private readonly String _directory = Path.Combine(Path.GetTempPath(), $"rdt-exe-block-{Guid.NewGuid():N}");
    private readonly TestSettings _settings = new();
    private readonly TorrentRunnerState _runnerState = new();
    private readonly Mock<IUsenetApi> _usenetApi = new(MockBehavior.Strict);

    private String DatabasePath => Path.Combine(_directory, "rdtclient.db");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);

        _settings.Current.Provider.Provider = Provider.TorBox;
        _settings.Current.Provider.ApiKey = Services.StalledJobs.Fixtures.DummyApiKey;
        _settings.Current.Provider.MaxParallelDownloads = 5;
        _settings.Current.Provider.StalledNzbTimeoutHours = 0;
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
        return new(new DbContextOptionsBuilder<DataContext>()
                   .UseSqlite(new SqliteConnectionStringBuilder { DataSource = DatabasePath, ForeignKeys = true }.ToString())
                   .Options);
    }

    private (TorrentRunner runner, Torrents torrents, DataContext context) Create()
    {
        var context = CreateContext();
        var data = new TorrentData(context);

        var client = new Mock<TorBoxDebridClient>(Mock.Of<ILogger<TorBoxDebridClient>>(),
                                                  new Mock<IHttpClientFactory>(MockBehavior.Strict).Object,
                                                  Mock.Of<IDownloadableFileFilter>(),
                                                  new RateLimitCoordinator(),
                                                  _settings)
        {
            CallBase = true
        };

        var torBox = new Mock<ITorBoxNetClient>(MockBehavior.Strict);
        torBox.Setup(m => m.Usenet).Returns(_usenetApi.Object);
        client.Protected().Setup<ITorBoxNetClient>("GetClient", ItExpr.IsAny<String>()).Returns(torBox.Object);

        var torrents = new Torrents(Mock.Of<ILogger<Torrents>>(), data, Mock.Of<IDownloads>(), null!, null!, null!, null!, null!, null!, null!, client.Object, null!, _settings, _runnerState);

        var runner = new TorrentRunner(Mock.Of<ILogger<TorrentRunner>>(), torrents, new(null!), new(null!, torrents), Mock.Of<IHttpClientFactory>(), new RateLimitCoordinator(), _settings, _runnerState);

        return (runner, torrents, context);
    }

    private void AllowNzbUpload(String returnedHash)
    {
        _usenetApi.Setup(m => m.AddFileAsync(It.IsAny<Byte[]>(), It.IsAny<Int32>(), It.IsAny<String>(), It.IsAny<String?>(), It.IsAny<Boolean>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new Response<UsenetAddResult> { Data = new() { Hash = returnedHash } });

        _usenetApi.Setup(m => m.GetHashInfoAsync(returnedHash, true, It.IsAny<Int32>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new UsenetInfoResult { Hash = returnedHash, Name = "Synthetic", DownloadState = "downloading", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
    }

    private void VerifyNeverUploaded()
    {
        _usenetApi.Verify(m => m.AddFileAsync(It.IsAny<Byte[]>(), It.IsAny<Int32>(), It.IsAny<String>(), It.IsAny<String?>(), It.IsAny<Boolean>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private async Task<Torrent> Reload(Guid id)
    {
        await using var context = CreateContext();

        return (await new TorrentData(context).GetById(id))!;
    }

    private static Byte[] ExeNzb()
    {
        return Encoding.UTF8.GetBytes(ExecutableFileDetectorTest.Nzb("[1/2] - \"Synthetic.Show.S01E01.exe\" yEnc (1/50)", "[2/2] - \"Synthetic.Show.S01E01.par2\" yEnc (1/1)"));
    }

    private static Byte[] CleanNzb()
    {
        return Encoding.UTF8.GetBytes(ExecutableFileDetectorTest.Nzb("[1/2] - \"Synthetic.Show.S01E02.mkv\" yEnc (1/900)", "[2/2] - \"Synthetic.Show.S01E02.par2\" yEnc (1/1)"));
    }

    private static Torrent Template()
    {
        return new()
        {
            TorrentRetryAttempts = 1,
            DownloadClient = RdtClient.Data.Enums.DownloadClient.Bezzad,
            HostDownloadAction = TorrentHostDownloadAction.DownloadAll
        };
    }

    [Fact]
    public async Task UploadedNzbWithExe_IsFailedOnAdd_NeverSentToProvider_ShownFailedToSonarr()
    {
        var (runner, torrents, context) = Create();

        await using var _ = context;

        var added = await torrents.AddNzbFileToDebridQueue(ExeNzb(), "Synthetic.Show.S01E01.nzb", Template());

        var blocked = await Reload(added.TorrentId);
        Assert.NotNull(blocked.Completed);
        Assert.StartsWith(ExecutableFileDetector.ErrorPrefix, blocked.Error);
        Assert.Contains("Synthetic.Show.S01E01.exe", blocked.Error);
        Assert.Null(blocked.Retry);
        Assert.Equal(blocked.TorrentRetryAttempts, blocked.RetryCount);
        Assert.Null(blocked.RdId);

        await runner.Tick();
        await runner.Tick();

        VerifyNeverUploaded();
        Assert.Null((await Reload(added.TorrentId)).Retry);

        var sab = new Sabnzbd(Mock.Of<ILogger<Sabnzbd>>(), torrents, new AppSettings(), _settings);
        Assert.Equal("Failed", Assert.Single((await sab.GetHistory()).Slots).Status);
        Assert.Empty((await sab.GetQueue()).Slots);
    }

    [Fact]
    public async Task CleanNzb_IsSubmittedNormally()
    {
        var (runner, torrents, context) = Create();

        await using var _ = context;
        AllowNzbUpload("synthetic0clean0hash000000000000d");

        var added = await torrents.AddNzbFileToDebridQueue(CleanNzb(), "Synthetic.Show.S01E02.nzb", Template());
        Assert.Null((await Reload(added.TorrentId)).Error);

        await runner.Tick();

        Assert.Equal("synthetic0clean0hash000000000000d", (await Reload(added.TorrentId)).RdId);
    }

    [Fact]
    public async Task SettingOff_ExeNzbIsNotBlocked()
    {
        _settings.Current.General.BlockExecutableDownloads = false;
        var (runner, torrents, context) = Create();

        await using var _ = context;
        AllowNzbUpload("synthetic0off0hash00000000000000e");

        var added = await torrents.AddNzbFileToDebridQueue(ExeNzb(), "Synthetic.Show.S01E01.nzb", Template());
        Assert.Null((await Reload(added.TorrentId)).Error);

        await runner.Tick();

        Assert.Equal("synthetic0off0hash00000000000000e", (await Reload(added.TorrentId)).RdId);
    }

    [Fact]
    public async Task QueuedBeforeSettingWasOn_IsBlockedAtSubmission_NotSent()
    {
        _settings.Current.General.BlockExecutableDownloads = false;
        var (runner, torrents, context) = Create();

        await using var _ = context;

        var added = await torrents.AddNzbFileToDebridQueue(ExeNzb(), "Synthetic.Show.S01E01.nzb", Template());
        Assert.Null((await Reload(added.TorrentId)).Error);

        _settings.Current.General.BlockExecutableDownloads = true;
        await runner.Tick();

        var blocked = await Reload(added.TorrentId);
        Assert.StartsWith(ExecutableFileDetector.ErrorPrefix, blocked.Error);
        Assert.Null(blocked.RdId);
        VerifyNeverUploaded();
    }

    [Theory]
    [InlineData("a8f3c0d9e1b2/Synthetic.Show.S01E03.exe", true)]
    [InlineData("a8f3c0d9e1b2/Synthetic.Show.S01E03.mkv", false)]
    public async Task ProviderFileList_WithExe_BlockedBeforeLocalDownload(String providerFile, Boolean expectBlocked)
    {
        // A submitted job (e.g. obfuscated NZB or NZB link) whose provider file list is now known.
        Guid id;

        await using (var seed = CreateContext())
        {
            var torrent = new Torrent
            {
                TorrentId = Guid.NewGuid(),
                Hash = "local-provider-exe",
                RdId = "synthetic0provider0hash000000000f",
                RdName = "Synthetic.Show.S01E03",
                Type = DownloadType.Nzb,
                ClientKind = Provider.TorBox,
                Added = DateTimeOffset.UtcNow,
                RdAdded = DateTimeOffset.UtcNow,
                RdStatus = TorrentStatus.Downloading,
                TorrentRetryAttempts = 1,
                HostDownloadAction = TorrentHostDownloadAction.DownloadAll,
                DownloadClient = RdtClient.Data.Enums.DownloadClient.Bezzad,
                RdFiles = System.Text.Json.JsonSerializer.Serialize(new List<DebridClientFile>
                {
                    new() { Id = 1, Path = providerFile, Bytes = 1000 },
                    new() { Id = 2, Path = "a8f3c0d9e1b2/readme.nfo", Bytes = 10 }
                })
            };

            seed.Torrents.Add(torrent);
            await seed.SaveChangesAsync();
            id = torrent.TorrentId;
        }

        var (runner, _, context) = Create();

        await using var _ = context;

        await runner.Tick();

        var after = await Reload(id);

        if (expectBlocked)
        {
            Assert.NotNull(after.Completed);
            Assert.StartsWith(ExecutableFileDetector.ErrorPrefix, after.Error);
            Assert.Contains("Synthetic.Show.S01E03.exe", after.Error);
            Assert.Empty(after.Downloads);
            Assert.Null(after.Retry);
            Assert.Equal("synthetic0provider0hash000000000f", after.RdId); // provider job not deleted
        }
        else
        {
            Assert.Null(after.Completed);
            Assert.Null(after.Error);
        }

        _usenetApi.Verify(m => m.ControlAsync(It.IsAny<String>(), It.IsAny<String>(), It.IsAny<Boolean>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private const String AbortedStatus = "failed (Aborted, cannot be completed - https://sabnzbd.org/not-complete)";

    private async Task<Guid> SeedProviderError(String rawStatus, DownloadType type = DownloadType.Nzb)
    {
        await using var seed = CreateContext();

        var torrent = new Torrent
        {
            TorrentId = Guid.NewGuid(),
            Hash = $"local-err-{Guid.NewGuid():N}",
            RdId = "synthetic0err0hash00000000000000a",
            RdName = "Synthetic.Show.S01E04",
            Type = type,
            ClientKind = Provider.TorBox,
            Added = DateTimeOffset.UtcNow,
            RdAdded = DateTimeOffset.UtcNow,
            RdStatus = TorrentStatus.Error,
            RdStatusRaw = rawStatus,
            TorrentRetryAttempts = 2,
            RetryCount = 0,
            HostDownloadAction = TorrentHostDownloadAction.DownloadAll,
            DownloadClient = RdtClient.Data.Enums.DownloadClient.Bezzad
        };

        seed.Torrents.Add(torrent);
        await seed.SaveChangesAsync();

        return torrent.TorrentId;
    }

    [Theory]
    [InlineData(AbortedStatus)]
    [InlineData("missing")]
    [InlineData("failed (Download failed - missing articles)")]
    public async Task IncompleteNzb_IsFailedWithoutRetry_AndNeverResubmitted(String rawStatus)
    {
        var id = await SeedProviderError(rawStatus);
        var (runner, torrents, context) = Create();

        await using var _ = context;

        for (var i = 0; i < 4; i++)
        {
            await runner.Tick();
        }

        var after = await Reload(id);
        Assert.NotNull(after.Completed);
        Assert.Equal($"Debrid error: {rawStatus}. Not retried: the NZB cannot be completed.", after.Error);
        Assert.Null(after.Retry);
        Assert.Equal(2, after.RetryCount);
        Assert.Equal("synthetic0err0hash00000000000000a", after.RdId);

        VerifyNeverUploaded();
        _usenetApi.Verify(m => m.ControlAsync(It.IsAny<String>(), It.IsAny<String>(), It.IsAny<Boolean>(), It.IsAny<CancellationToken>()), Times.Never);

        var sab = new Sabnzbd(Mock.Of<ILogger<Sabnzbd>>(), torrents, new AppSettings(), _settings);
        Assert.Equal("Failed", Assert.Single((await sab.GetHistory()).Slots).Status);
    }

    [Fact]
    public async Task OtherProviderError_StillUsesNormalRetry()
    {
        var id = await SeedProviderError("failed (Some transient provider problem)");
        var (runner, _, context) = Create();

        await using var _ = context;

        await runner.Tick();

        var after = await Reload(id);
        Assert.NotNull(after.Retry); // retry scheduled exactly as upstream
        Assert.Equal(1, after.RetryCount);
    }

    [Fact]
    public async Task SettingOff_IncompleteNzbUsesNormalRetry()
    {
        _settings.Current.Provider.NoRetryIncompleteNzbs = false;
        var id = await SeedProviderError(AbortedStatus);
        var (runner, _, context) = Create();

        await using var _ = context;

        await runner.Tick();

        Assert.NotNull((await Reload(id)).Retry);
    }

    [Fact]
    public async Task TorrentWithSameText_UsesNormalRetry()
    {
        var id = await SeedProviderError(AbortedStatus, DownloadType.Torrent);
        var (runner, _, context) = Create();

        await using var _ = context;

        await runner.Tick();

        Assert.NotNull((await Reload(id)).Retry);
    }

    [Theory]
    [InlineData(AbortedStatus, true)]
    [InlineData("MISSING", true)]
    [InlineData("failed (Aborted, cannot be completed)", true)]
    [InlineData("failed", false)]
    [InlineData("error", false)]
    [InlineData("failed (Server error, try again)", false)]
    [InlineData("missing_files_in_progress", false)]
    [InlineData(null, false)]
    public void IsIncompleteNzbFailure(String? raw, Boolean expected)
    {
        Assert.Equal(expected, TorrentRunner.IsIncompleteNzbFailure(raw));
    }

    [Fact]
    public async Task JobAlreadyDownloadingLocally_IsNotInterrupted()
    {
        var torrent = new Torrent
        {
            RdStatus = TorrentStatus.Finished,
            Downloads = [new() { DownloadId = Guid.NewGuid() }]
        };

        var (_, torrents, context) = Create();

        await using var _ = context;

        await torrents.BlockExecutableTorrent(torrent, ["x.exe"]);

        Assert.Null(torrent.Error);
        Assert.Null(torrent.Completed);
    }
}
