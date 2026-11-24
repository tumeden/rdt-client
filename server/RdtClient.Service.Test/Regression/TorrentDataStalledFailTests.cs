using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RdtClient.Data.Data;
using RdtClient.Data.Enums;
using RdtClient.Data.Models.Data;

namespace RdtClient.Service.Test.Regression;

/// <summary>
///     Real SQLite tests of the single conditional UPDATE used by the stall watchdog. Each case simulates a change
///     that lands after the watchdog's read but before its write; the write must then change nothing.
///     SYNTHETIC DATA ONLY.
/// </summary>
public class TorrentDataStalledFailTests : IAsyncLifetime
{
    private const String Hash = "synthetic0race0hash000000000000c";
    private readonly String _databasePath = Path.Combine(Path.GetTempPath(), $"rdt-stall-cas-{Guid.NewGuid():N}.sqlite");

    public async Task InitializeAsync()
    {
        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_databasePath);

        return Task.CompletedTask;
    }

    private DataContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<DataContext>()
                      .UseSqlite(new SqliteConnectionStringBuilder { DataSource = _databasePath, ForeignKeys = true }.ToString())
                      .Options;

        return new(options);
    }

    private async Task<Guid> SeedStalled(Action<Torrent>? mutate = null)
    {
        var torrent = new Torrent
        {
            TorrentId = Guid.NewGuid(),
            Hash = "local-race",
            RdId = Hash,
            Type = DownloadType.Nzb,
            ClientKind = Provider.TorBox,
            Added = DateTimeOffset.UtcNow.AddDays(-3),
            RdAdded = DateTimeOffset.UtcNow.AddDays(-3),
            RdStatus = TorrentStatus.Downloading,
            RdProgress = 100,
            TorrentRetryAttempts = 2,
            RetryCount = 0
        };

        mutate?.Invoke(torrent);

        await using var context = CreateContext();
        context.Torrents.Add(torrent);
        await context.SaveChangesAsync();

        return torrent.TorrentId;
    }

    private async Task<Boolean> TryFail(Guid id, String rdId = Hash)
    {
        await using var context = CreateContext();

        return await new TorrentData(context).TryMarkStalledFailed(id, rdId, "Stalled at TorBox: test", DateTimeOffset.UtcNow);
    }

    private async Task<Torrent> Load(Guid id)
    {
        await using var context = CreateContext();

        return await context.Torrents.AsNoTracking().SingleAsync(m => m.TorrentId == id);
    }

    [Fact]
    public async Task IdleStalledJob_IsFailed_RetriesExhausted_NoRetryScheduled()
    {
        var id = await SeedStalled();

        Assert.True(await TryFail(id));

        var t = await Load(id);
        Assert.NotNull(t.Completed);
        Assert.Equal("Stalled at TorBox: test", t.Error);
        Assert.Equal(2, t.RetryCount);
        Assert.Null(t.Retry);
        Assert.Equal(TorrentStatus.Downloading, t.RdStatus);
        Assert.Equal(Hash, t.RdId);
    }

    public static TheoryData<String, Action<Torrent>> ConcurrentChanges => new()
    {
        { "completed meanwhile", t => t.Completed = DateTimeOffset.UtcNow },
        { "errored meanwhile", t => t.Error = "other" },
        { "retry scheduled", t => t.Retry = DateTimeOffset.UtcNow },
        { "provider finished", t => t.RdStatus = TorrentStatus.Finished },
        { "provider error", t => t.RdStatus = TorrentStatus.Error },
        { "requeued locally", t => t.RdStatus = TorrentStatus.Queued },
        { "RdId replaced", t => t.RdId = "another-hash" },
        { "unsubmitted", t => t.RdAdded = null },
        { "torrent type", t => t.Type = DownloadType.Torrent }
    };

    [Theory]
    [MemberData(nameof(ConcurrentChanges))]
    public async Task ChangedAfterRead_WriteIsRejected(String label, Action<Torrent> change)
    {
        var id = await SeedStalled(change);
        var before = await Load(id);

        Assert.False(await TryFail(id), label);

        var after = await Load(id);
        Assert.Equal(before.Completed, after.Completed);
        Assert.Equal(before.Error, after.Error);
        Assert.Equal(before.RetryCount, after.RetryCount);
    }

    [Fact]
    public async Task DownloadRowCreatedAfterRead_WriteIsRejected()
    {
        var id = await SeedStalled();

        await using (var context = CreateContext())
        {
            context.Downloads.Add(new() { DownloadId = Guid.NewGuid(), TorrentId = id, Path = "synthetic.mkv", Added = DateTimeOffset.UtcNow });
            await context.SaveChangesAsync();
        }

        Assert.False(await TryFail(id));
        Assert.Null((await Load(id)).Completed);
    }

    [Fact]
    public async Task UnknownTorrent_ReturnsFalse()
    {
        Assert.False(await TryFail(Guid.NewGuid()));
    }
}
