using Microsoft.Extensions.Logging;
using Moq;
using RdtClient.Data.Data;
using RdtClient.Data.Enums;
using RdtClient.Data.Models.Data;
using RdtClient.Service.Helpers;
using RdtClient.Service.Services;

namespace RdtClient.Service.Test.Services;

public class TorrentRunnerTest
{
    [Theory]
    [InlineData(TorrentStatus.Queued, false)]
    [InlineData(TorrentStatus.Processing, true)]
    [InlineData(TorrentStatus.WaitingForFileSelection, true)]
    [InlineData(TorrentStatus.Downloading, true)]
    [InlineData(TorrentStatus.Finished, false)]
    [InlineData(TorrentStatus.Uploading, true)]
    [InlineData(TorrentStatus.Error, false)]
    public void OccupiesProviderSlot_ActiveTorrent_UnchangedFromUpstreamBehavior(TorrentStatus status, Boolean expected)
    {
        // Provider-agnostic: the same predicate is used for every debrid provider.
        foreach (var type in new[] { DownloadType.Torrent, DownloadType.Nzb })
        {
            Assert.Equal(expected, TorrentRunner.OccupiesProviderSlot(new() { RdStatus = status, Type = type }));
        }
    }

    [Theory]
    [InlineData(TorrentStatus.Processing)]
    [InlineData(TorrentStatus.WaitingForFileSelection)]
    [InlineData(TorrentStatus.Downloading)]
    [InlineData(TorrentStatus.Uploading)]
    public void OccupiesProviderSlot_TerminalTorrentWithActiveLookingStatus_DoesNotOccupy(TorrentStatus status)
    {
        Assert.False(TorrentRunner.OccupiesProviderSlot(new() { RdStatus = status, Completed = DateTimeOffset.UtcNow }));
        Assert.False(TorrentRunner.OccupiesProviderSlot(new() { RdStatus = status, Completed = DateTimeOffset.UtcNow, Error = "Torrent lifetime of 1 minutes reached" }));
        Assert.False(TorrentRunner.OccupiesProviderSlot(new() { RdStatus = status, Error = "Could not add to provider" }));
    }

    [Fact]
    public async Task Tick_ShouldNotRequeueCompletedErrorTorrent()
    {
        var testSettings = new TestSettings();
        var runnerState = new TorrentRunnerState();

        testSettings.Current.Provider.ApiKey = "test-api-key";
        testSettings.Current.Provider.Provider = Provider.RealDebrid;
        testSettings.Current.Provider.MaxParallelDownloads = 1;
        testSettings.Current.DownloadClient.DownloadPath = "/downloads";

        var erroredTorrent = new Torrent
        {
            TorrentId = Guid.NewGuid(),
            Hash = "hash-1",
            RdName = "Torrent 1",
            Payload = new()
            {
                Content = "magnet:?xt=urn:btih:hash-1"
            },
            Type = DownloadType.Torrent,
            RdStatus = TorrentStatus.Queued,
            DeleteOnError = 10,
            Error = "Could not add to provider: Infringing file",
            Completed = DateTimeOffset.UtcNow.AddMinutes(-5),
            Downloads = new List<Download>()
        };

        var torrentDataMock = new Mock<ITorrentData>(MockBehavior.Strict);

        torrentDataMock.Setup(m => m.Get())
                       .ReturnsAsync(new List<Torrent>
                       {
                           erroredTorrent
                       });

        var torrents = new Torrents(Mock.Of<ILogger<Torrents>>(),
                                    torrentDataMock.Object,
                                    Mock.Of<IDownloads>(),
                                    null!,
                                    null!,
                                    null!,
                                    null!,
                                    null!,
                                    null!,
                                    null!,
                                    null!,
                                    null!,
                                    testSettings,
                                    runnerState);

        var torrentRunner = new TorrentRunner(Mock.Of<ILogger<TorrentRunner>>(),
                                              torrents,
                                              new(null!),
                                              new(null!, torrents),
                                              Mock.Of<IHttpClientFactory>(),
                                              new RateLimitCoordinator(),
                                              testSettings,
                                              runnerState);

        await torrentRunner.Tick();

        torrentDataMock.Verify(m => m.UpdateComplete(It.IsAny<Guid>(),
                                                     It.IsAny<String?>(),
                                                     It.IsAny<DateTimeOffset?>(),
                                                     It.IsAny<Boolean>()),
                               Times.Never);
    }
}
