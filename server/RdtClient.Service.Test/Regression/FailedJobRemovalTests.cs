using System.Text;
using System.Text.Json;
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
using RdtClient.Service.Test.Helpers;
using RdtClient.Service.Test.Services.StalledJobs;
using TorBoxNET;
using Torrent = RdtClient.Data.Models.Data.Torrent;

namespace RdtClient.Service.Test.Regression;

/// <summary>
///     Automatic removal of failed jobs through the main setting, using real SQLite, the real scheduler and the real
///     watchdog. The TorBox API is mocked (strict) and files live in a temp folder. SYNTHETIC DATA ONLY.
/// </summary>
[Collection(StalledJobCollection.Name)]
public class FailedJobRemovalTests : IAsyncLifetime
{
    private readonly String _directory = Path.Combine(Path.GetTempPath(), $"rdt-failed-removal-{Guid.NewGuid():N}");
    private readonly TestSettings _settings = new();
    private readonly TorrentRunnerState _runnerState = new();
    private readonly Dictionary<Int64, (DownloadType Type, String Hash)> _torBoxJobs = [];
    private readonly List<Int64> _deleted = [];
    private Int32 _lists;
    private Func<Int64, Task> _onDelete = _ => Task.CompletedTask;
    private String? _listOverride;
    private readonly ManualTimeProvider _clock = new(DateTimeOffset.UtcNow.AddHours(-2));

    private String DownloadRoot => Path.Combine(_directory, "downloads");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(DownloadRoot);

        _settings.Current.Provider.Provider = Provider.TorBox;
        _settings.Current.Provider.ApiKey = Fixtures.DummyApiKey;
        _settings.Current.Provider.MaxParallelDownloads = 1;
        _settings.Current.Provider.StalledNzbTimeoutHours = 6;
        _settings.Current.DownloadClient.DownloadPath = DownloadRoot;
        _settings.Current.Integrations.Default.DeleteOnError = 30;
        _runnerState.FailedRemovals.Interval = TimeSpan.Zero;

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
        var connection = new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "rdtclient.db"), ForeignKeys = true }.ToString();

        return new(new DbContextOptionsBuilder<DataContext>().UseSqlite(connection).Options);
    }

    private (TorrentRunner runner, Torrents torrents, StalledJobWatchdog watchdog) Create(DataContext context, String? watchdogJson = null)
    {
        var torBox = new Mock<TorBoxDebridClient>(Mock.Of<ILogger<TorBoxDebridClient>>(),
                                                  new Mock<IHttpClientFactory>(MockBehavior.Strict).Object,
                                                  Mock.Of<IDownloadableFileFilter>(),
                                                  new RateLimitCoordinator(),
                                                  _settings)
        {
            CallBase = true
        };

        var api = new Mock<ITorBoxNetClient>(MockBehavior.Strict);
        torBox.Protected().Setup<ITorBoxNetClient>("GetClient", ItExpr.IsAny<String>()).Returns(api.Object);
        torBox.Setup(m => m.GetUsenetMyListJson()).ReturnsAsync(() => watchdogJson ?? ListJson(DownloadType.Nzb));
        torBox.Setup(m => m.GetTorrentMyListJson()).ReturnsAsync(() => ListJson(DownloadType.Torrent));

        torBox.Setup(m => m.DeleteProviderJob(It.IsAny<DownloadType>(), It.IsAny<Int64>()))
              .Returns(async (DownloadType _, Int64 id) =>
              {
                  _deleted.Add(id);
                  await _onDelete(id);
                  _torBoxJobs.Remove(id);
              });

        var data = new TorrentData(context);
        var downloads = new Downloads(new DownloadData(context));
        var torrents = new Torrents(Mock.Of<ILogger<Torrents>>(), data, downloads, null!, null!, null!, null!, null!, null!, null!, torBox.Object, null!, _settings, _runnerState);
        var runner = new TorrentRunner(Mock.Of<ILogger<TorrentRunner>>(), torrents, downloads, new(null!, torrents), Mock.Of<IHttpClientFactory>(), new RateLimitCoordinator(), _settings, _runnerState);

        var watchdog = new StalledJobWatchdog(Mock.Of<ILogger<StalledJobWatchdog>>(),
                                              data,
                                              new TorBoxUsenetSnapshotSource(torBox.Object),
                                              new InMemoryStateStore(),
                                              new RateLimitCoordinator(),
                                              _settings,
                                              _runnerState,
                                              _clock);

        return (runner, torrents, watchdog);
    }

    private static Torrent Job(String name, Int32 failedMinutesAgo, Int32 ownValue = 0)
    {
        return new()
        {
            TorrentId = Guid.NewGuid(),
            Hash = $"local-{name}",
            RdId = $"provider-{name}",
            RdName = name,
            Category = "tv",
            Type = DownloadType.Nzb,
            ClientKind = Provider.TorBox,
            Added = DateTimeOffset.UtcNow.AddDays(-1),
            RdAdded = DateTimeOffset.UtcNow.AddDays(-1),
            RdStatus = TorrentStatus.Error,
            Error = "Debrid error: synthetic",
            Completed = DateTimeOffset.UtcNow.AddMinutes(-failedMinutesAgo),
            DeleteOnError = ownValue,
            DownloadClient = RdtClient.Data.Enums.DownloadClient.Bezzad
        };
    }

    private String ListJson(DownloadType type)
    {
        _lists++;

        return _listOverride ?? JsonSerializer.Serialize(new { success = true, data = _torBoxJobs.Where(m => m.Value.Type == type).Select(m => new { id = m.Key, hash = m.Value.Hash, active = false }) });
    }

    private async Task Seed(params Object[] rows)
    {
        // Every submitted NZB also exists at the (fake) provider, once per hash.
        foreach (var torrent in rows.OfType<Torrent>().Where(m => m is { Type: DownloadType.Nzb, RdId: not null }))
        {
            if (_torBoxJobs.Values.All(m => m.Hash != torrent.RdId))
            {
                _torBoxJobs[1000 + _torBoxJobs.Count + _deleted.Count] = (DownloadType.Nzb, torrent.RdId!);
            }
        }

        await using var context = CreateContext();
        context.AddRange(rows);
        await context.SaveChangesAsync();
    }

    private async Task Tick(Int32 times = 6)
    {
        await using var context = CreateContext();
        var runner = Create(context).runner;

        for (var i = 0; i < times; i++)
        {
            await runner.Tick();
        }
    }

    private async Task<Boolean> Exists(Torrent torrent)
    {
        await using var context = CreateContext();

        return await context.Torrents.AnyAsync(m => m.TorrentId == torrent.TorrentId);
    }

    private async Task<Int32> CountExisting(IEnumerable<Torrent> jobs)
    {
        var ids = jobs.Select(m => m.TorrentId).ToList();
        await using var context = CreateContext();

        return await context.Torrents.CountAsync(m => ids.Contains(m.TorrentId));
    }

    private String CreateFolder(String name)
    {
        var folder = Path.Combine(DownloadRoot, "tv", name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "synthetic.mkv"), "x");

        return folder;
    }

    private static Download FinishedDownload(Torrent torrent)
    {
        return new()
        {
            DownloadId = Guid.NewGuid(),
            TorrentId = torrent.TorrentId,
            Path = "synthetic",
            Link = "https://download.invalid/synthetic.mkv",
            FileName = "synthetic.mkv",
            Added = DateTimeOffset.UtcNow,
            DownloadStarted = DateTimeOffset.UtcNow.AddHours(-1),
            Completed = DateTimeOffset.UtcNow
        };
    }

    private void VerifyProviderDeletes(Int32 expected)
    {
        Assert.Equal(expected, _deleted.Count);
    }

    [Theory]
    [InlineData(0, 30, 31, true)] // existing job with no own value follows the main setting
    [InlineData(0, 30, 29, false)] // not due yet
    [InlineData(0, 0, 5000, false)] // main setting off
    [InlineData(60, 30, 45, false)] // own value wins (longer)
    [InlineData(10, 30, 15, true)] // own value wins (shorter)
    [InlineData(10, 0, 15, true)] // own value still works with the main setting off
    public async Task FailedJob_IsRemoved_ByMainSettingOrOwnValue(Int32 own, Int32 main, Int32 failedMinutesAgo, Boolean removed)
    {
        _settings.Current.Integrations.Default.DeleteOnError = main;
        var job = Job("a", failedMinutesAgo, own);
        await Seed(job);

        await Tick();

        Assert.Equal(!removed, await Exists(job));
        VerifyProviderDeletes(removed ? 1 : 0);
    }

    [Fact]
    public void Default_IsThirtyMinutes_OnlyForTheMainSetting()
    {
        var settings = new DbSettings();

        Assert.Equal(30, settings.Integrations.Default.DeleteOnError);
        Assert.Equal(0, settings.Gui.Default.DeleteOnError);
        Assert.Equal(0, settings.Watch.Default.DeleteOnError);
        Assert.Equal(0, settings.Provider.Default.DeleteOnError);
    }

    [Theory]
    [InlineData(null, "30")] // new install
    [InlineData("0", "0")] // saved earlier, kept as it is
    [InlineData("45", "45")]
    public async Task Default_IsUsedForNewInstalls_AndNeverReplacesASavedValue(String? saved, String expected)
    {
        const String key = "Integrations:Default:DeleteOnError";

        await using var context = CreateContext();

        if (saved != null)
        {
            context.Settings.Add(new() { SettingId = key, Value = saved });
            await context.SaveChangesAsync();
        }

        await new SettingData(context, Mock.Of<ILogger<SettingData>>()).Seed();

        Assert.Equal(expected, (await context.Settings.AsNoTracking().SingleAsync(m => m.SettingId == key)).Value);
    }

    [Fact]
    public async Task ChangingTheMainSetting_AppliesToJobsAlreadyListed_OnTheNextTick()
    {
        _settings.Current.Integrations.Default.DeleteOnError = 0;
        var jobs = Enumerable.Range(0, 5).Select(i => Job($"old{i}", 600)).ToList();
        await Seed(jobs.Cast<Object>().ToArray());

        await Tick();
        Assert.Equal(5, await CountExisting(jobs));

        _settings.Current.Integrations.Default.DeleteOnError = 30;
        _runnerState.FailedRemovals.Interval = TimeSpan.Zero;
        await Tick();
        Assert.Equal(0, await CountExisting(jobs));
    }

    [Fact]
    public async Task JobsThatDidNotFail_AreNeverRemoved()
    {
        var finished = Job("finished", 5000);
        finished.Error = null;
        finished.RdStatus = TorrentStatus.Finished;
        finished.FinishedAction = TorrentFinishedAction.None;

        var active = Job("active", 0);
        active.Error = null;
        active.Completed = null;
        active.RdStatus = TorrentStatus.Downloading;

        var queued = Job("queued", 0);
        queued.Error = null;
        queued.Completed = null;
        queued.RdId = null;
        queued.RdAdded = null;
        queued.RdStatus = TorrentStatus.Queued;
        queued.Added = DateTimeOffset.UtcNow.AddDays(-30);

        await Seed(finished, active, queued);
        await Tick();

        Assert.Equal(3, await CountExisting([finished, active, queued]));
        VerifyProviderDeletes(0);
    }

    [Fact]
    public async Task StalledJob_WithRemovalOn_IsFailed_FreesItsSlot_ThenLeavesTheList()
    {
        // The reported conflict, through both routes: the main setting and a copied per-job value of 30.
        var updated = DateTimeOffset.UtcNow.AddDays(-3).ToString("O");
        Torrent[] stuck = [Job("stuck-main", 0), Job("stuck-own", 0, 30)];

        foreach (var job in stuck)
        {
            job.Error = null;
            job.Completed = null;
            job.RdStatus = TorrentStatus.Downloading;
        }

        await Seed(stuck.Cast<Object>().ToArray());

        var json = JsonSerializer.Serialize(new
        {
            success = true,
            data = stuck.Select((j, i) => new
            {
                id = i + 1,
                hash = j.RdId,
                download_state = "processing",
                progress = 1,
                active = false,
                download_present = false,
                download_finished = true,
                size = 1,
                created_at = updated,
                updated_at = updated
            })
        });

        await using (var context = CreateContext())
        {
            var (_, _, watchdog) = Create(context, json);

            Assert.Empty((await watchdog.RunOnce()).Failed);
            _clock.Advance(TimeSpan.FromHours(1));
            Assert.Equal(2, (await watchdog.RunOnce()).Failed.Count);
        }

        await using (var context = CreateContext())
        {
            foreach (var job in await context.Torrents.AsNoTracking().ToListAsync())
            {
                Assert.StartsWith("Stalled at TorBox", job.Error);
                Assert.False(TorrentRunner.OccupiesProviderSlot(job));
            }
        }

        VerifyProviderDeletes(0); // the watchdog itself deletes nothing

        await Tick(); // failed an hour ago on the watchdog clock, so the 30 minutes have passed

        Assert.Equal(0, await CountExisting(stuck));
        VerifyProviderDeletes(2);
    }

    [Fact]
    public async Task OwnDownloadedFiles_AreDeleted()
    {
        var job = Job("partial", 31);
        var folder = CreateFolder("partial");
        await Seed(job, FinishedDownload(job));

        await Tick();

        Assert.False(await Exists(job));
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task JobThatNeverDownloaded_DoesNotDeleteAFolderWithTheSameName()
    {
        var job = Job("same-name", 31);
        var folder = CreateFolder("same-name");
        await Seed(job);

        await Tick();

        Assert.False(await Exists(job));
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public async Task RegrabWithTheSameName_KeepsItsFolderAndProviderJob()
    {
        var failed = Job("regrab", 31);
        var good = Job("regrab", 0);
        good.Hash = "local-regrab-2";
        good.Error = null;
        good.RdStatus = TorrentStatus.Finished;
        good.FinishedAction = TorrentFinishedAction.None;
        var folder = CreateFolder("regrab");
        await Seed(failed, good, FinishedDownload(failed), FinishedDownload(good));

        await Tick();

        Assert.False(await Exists(failed));
        Assert.True(await Exists(good));
        Assert.True(Directory.Exists(folder));
        VerifyProviderDeletes(0); // same provider id, still owned by the good job
    }

    [Fact]
    public async Task TwoFailedJobsSharingOneProviderJob_DeleteItOnce()
    {
        var first = Job("twin", 40);
        var second = Job("twin", 35);
        second.Hash = "local-twin-2";
        await Seed(first, second);

        await Tick();

        Assert.Equal(0, await CountExisting([first, second]));
        VerifyProviderDeletes(1);
    }

    [Fact]
    public async Task SonarrJob_StoresNoOwnValue_SoLaterSettingChangesApply()
    {
        await using var context = CreateContext();
        var (_, torrents, _) = Create(context);
        var sab = new Sabnzbd(Mock.Of<ILogger<Sabnzbd>>(), torrents, new AppSettings(), _settings);

        await sab.AddFile(Encoding.UTF8.GetBytes(ExecutableFileDetectorTest.Nzb("\"Synthetic.mkv\" yEnc (1/1)")), "Synthetic.nzb", "tv", null);

        var added = await context.Torrents.AsNoTracking().SingleAsync();
        Assert.Equal(0, added.DeleteOnError);
        Assert.Equal(30, TorrentRunner.GetDeleteOnErrorMinutes(added, _settings.Current));
    }

    private async Task<Boolean> RemoveFailed(Torrent torrent)
    {
        await using var context = CreateContext();

        return await Create(context).torrents.RemoveFailed(torrent.TorrentId);
    }

    private async Task Change(Torrent torrent, Action<Torrent> change)
    {
        await using var context = CreateContext();
        change(await context.Torrents.SingleAsync(m => m.TorrentId == torrent.TorrentId));
        await context.SaveChangesAsync();
    }

    private static Torrent Regrab(String name)
    {
        var job = Job(name, 0);
        job.Hash = $"local-{name}-2";
        job.Error = null;
        job.Completed = null;
        job.RdStatus = TorrentStatus.Downloading;

        return job;
    }

    public static TheoryData<String, Action<Torrent>> ChangesAfterTheRead => new()
    {
        { "retry requested", t => t.Retry = DateTimeOffset.UtcNow },
        { "download retried", t => t.Completed = null },
        { "error cleared", t => t.Error = null },
        { "failed again later", t => t.Completed = DateTimeOffset.UtcNow },
        { "other provider job", t => t.RdId = "another" },
        { "renamed", t => t.RdName = "another" },
        { "category changed", t => t.Category = "movies" },
        { "own delay changed", t => t.DeleteOnError = 600 }
    };

    [Theory]
    [MemberData(nameof(ChangesAfterTheRead))]
    public async Task ChangeBeforeTheListDelete_KeepsTheJob(String label, Action<Torrent> change)
    {
        var job = Job("cas", 31);
        await Seed(job, FinishedDownload(job));

        await using var context = CreateContext();
        var data = new TorrentData(context);
        var read = (await data.GetById(job.TorrentId))!;

        await Change(job, change);

        Assert.False(await data.TryDeleteFailed(read, () => true), label);
        Assert.True(await Exists(job));

        await using var verify = CreateContext();
        Assert.Equal(1, await verify.Downloads.CountAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DownloadAddedOrReopenedBeforeTheListDelete_KeepsTheJob(Boolean added)
    {
        var job = Job("dl", 31);
        var download = FinishedDownload(job);
        await Seed(job, download);

        await using var context = CreateContext();
        var data = new TorrentData(context);
        var read = (await data.GetById(job.TorrentId))!;

        await using (var change = CreateContext())
        {
            if (added)
            {
                var extra = FinishedDownload(job);
                extra.Path = "another";
                change.Downloads.Add(extra);
            }
            else
            {
                (await change.Downloads.SingleAsync()).Completed = null;
            }

            await change.SaveChangesAsync();
        }

        Assert.False(await data.TryDeleteFailed(read, () => true));
        Assert.True(await Exists(job));
    }

    [Fact]
    public async Task SettingTurnedOffAtTheLastMoment_RollsTheListDeleteBack()
    {
        var job = Job("off", 31);
        await Seed(job, FinishedDownload(job));

        await using var context = CreateContext();
        var data = new TorrentData(context);

        Assert.False(await data.TryDeleteFailed((await data.GetById(job.TorrentId))!, () => false));
        Assert.True(await Exists(job));

        await using var verify = CreateContext();
        Assert.Equal(1, await verify.Downloads.CountAsync());
    }

    [Fact]
    public async Task StaleCandidate_RetryOrSettingChange_IsNotRemoved()
    {
        var job = Job("stale", 31);
        var folder = CreateFolder("stale");
        await Seed(job, FinishedDownload(job));

        await Change(job, t => t.Retry = DateTimeOffset.UtcNow);
        Assert.False(await RemoveFailed(job));

        await Change(job, t => t.Retry = null);
        _settings.Current.Integrations.Default.DeleteOnError = 0;
        Assert.False(await RemoveFailed(job));

        Assert.True(await Exists(job));
        Assert.True(Directory.Exists(folder));
        VerifyProviderDeletes(0);
        Assert.Equal(0, _lists); // not even a lookup
    }

    [Fact]
    public async Task RegrabWhileWaitingForTheSubmissionLock_KeepsProviderJobAndFiles()
    {
        var failed = Job("late", 31);
        var folder = CreateFolder("late");
        await Seed(failed, FinishedDownload(failed));

        await Torrents.ProviderMutationLock.WaitAsync();
        Task<Boolean> removal;

        try
        {
            removal = RemoveFailed(failed);
            await Task.Delay(200);
            Assert.True(await Exists(failed)); // nothing happens before the lock is free
            await Seed(Regrab("late"));
        }
        finally
        {
            Torrents.ProviderMutationLock.Release();
        }

        Assert.True(await removal);
        Assert.True(File.Exists(Path.Combine(folder, "synthetic.mkv")));
        VerifyProviderDeletes(0);
    }

    [Fact]
    public async Task RegrabDuringTheProviderLookup_KeepsProviderJobAndFiles()
    {
        // The lookup happens before the last check, so a job that shows up during it must still win.
        var failed = Job("lookup", 31);
        var folder = CreateFolder("lookup");
        await Seed(failed, FinishedDownload(failed));

        var list = ListJson(DownloadType.Nzb);
        await using var context = CreateContext();
        var torBox = new Mock<TorBoxDebridClient>(Mock.Of<ILogger<TorBoxDebridClient>>(), Mock.Of<IHttpClientFactory>(), Mock.Of<IDownloadableFileFilter>(), new RateLimitCoordinator(), _settings) { CallBase = true };

        torBox.Setup(m => m.GetUsenetMyListJson()).Returns(async () =>
        {
            await Seed(Regrab("lookup"));

            return list;
        });

        torBox.Setup(m => m.DeleteProviderJob(It.IsAny<DownloadType>(), It.IsAny<Int64>())).Callback(() => _deleted.Add(1)).Returns(Task.CompletedTask);

        var torrents = new Torrents(Mock.Of<ILogger<Torrents>>(), new TorrentData(context), Mock.Of<IDownloads>(), null!, null!, null!, null!, null!, null!, null!, torBox.Object, null!, _settings, _runnerState);

        Assert.True(await torrents.RemoveFailed(failed.TorrentId));

        Assert.True(File.Exists(Path.Combine(folder, "synthetic.mkv")));
        VerifyProviderDeletes(0);
    }

    [Theory]
    [InlineData("retry")]
    [InlineData("setting")]
    [InlineData("provider id")]
    public async Task ChangeDuringTheProviderLookup_DeletesNothing(String change)
    {
        var failed = Job("midway", 31);
        var folder = CreateFolder("midway");
        await Seed(failed, FinishedDownload(failed));

        var list = ListJson(DownloadType.Nzb);
        await using var context = CreateContext();
        var torBox = new Mock<TorBoxDebridClient>(Mock.Of<ILogger<TorBoxDebridClient>>(), Mock.Of<IHttpClientFactory>(), Mock.Of<IDownloadableFileFilter>(), new RateLimitCoordinator(), _settings) { CallBase = true };

        torBox.Setup(m => m.GetUsenetMyListJson()).Returns(async () =>
        {
            switch (change)
            {
                case "retry":
                    await Change(failed, t => t.Retry = DateTimeOffset.UtcNow);

                    break;
                case "setting":
                    _settings.Current.Integrations.Default.DeleteOnError = 0;

                    break;
                default:
                    await Change(failed, t => t.RdId = "another");

                    break;
            }

            return list;
        });

        torBox.Setup(m => m.DeleteProviderJob(It.IsAny<DownloadType>(), It.IsAny<Int64>())).Callback(() => _deleted.Add(1)).Returns(Task.CompletedTask);

        var torrents = new Torrents(Mock.Of<ILogger<Torrents>>(), new TorrentData(context), Mock.Of<IDownloads>(), null!, null!, null!, null!, null!, null!, null!, torBox.Object, null!, _settings, _runnerState);

        Assert.False(await torrents.RemoveFailed(failed.TorrentId));

        Assert.True(await Exists(failed));
        Assert.True(File.Exists(Path.Combine(folder, "synthetic.mkv")));
        VerifyProviderDeletes(0);
    }

    [Fact]
    public async Task AddingAJob_CategoryChange_AndSettingsChange_WaitUntilTheCleanupIsDone()
    {
        var failed = Job("busy", 31);
        var bystander = Regrab("bystander");
        await Seed(failed, bystander);

        var deleting = new TaskCompletionSource();
        var finish = new TaskCompletionSource();

        _onDelete = async _ =>
        {
            deleting.TrySetResult();
            await finish.Task.WaitAsync(TimeSpan.FromSeconds(5));
        };

        var removal = RemoveFailed(failed);
        await deleting.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await using var addContext = CreateContext();
        await using var categoryContext = CreateContext();
        await using var updateContext = CreateContext();
        var nzb = Encoding.UTF8.GetBytes(ExecutableFileDetectorTest.Nzb("\"New.mkv\" yEnc (1/1)"));
        var add = Create(addContext).torrents.AddNzbFileToDebridQueue(nzb, "New.nzb", new() { DownloadClient = RdtClient.Data.Enums.DownloadClient.Bezzad });
        var category = Create(categoryContext).torrents.UpdateCategory(bystander.Hash, "movies");
        var update = Create(updateContext).torrents.Update(bystander);

        await Task.Delay(300);
        Assert.False(add.IsCompleted);
        Assert.False(category.IsCompleted);
        Assert.False(update.IsCompleted);

        finish.SetResult();
        Assert.True(await removal);
        await Task.WhenAll(add, category, update);

        Assert.False(await Exists(failed));
        Assert.True(await Exists(await add));
    }

    [Fact]
    public async Task JobRetry_AndDownloadRetry_WaitUntilTheCleanupIsDone()
    {
        var failed = Job("retrying", 31);
        var download = FinishedDownload(failed);
        await Seed(failed, download);

        var deleting = new TaskCompletionSource();
        var finish = new TaskCompletionSource();

        _onDelete = async _ =>
        {
            deleting.TrySetResult();
            await finish.Task.WaitAsync(TimeSpan.FromSeconds(5));
        };

        var removal = RemoveFailed(failed);
        await deleting.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await using var jobContext = CreateContext();
        await using var downloadContext = CreateContext();
        var jobRetry = Create(jobContext).torrents.RetryTorrent(failed.TorrentId, 0);
        var downloadRetry = Create(downloadContext).torrents.RetryDownload(download.DownloadId);

        await Task.Delay(300);
        Assert.False(jobRetry.IsCompleted);
        Assert.False(downloadRetry.IsCompleted);

        finish.SetResult();
        Assert.True(await removal);
        await Task.WhenAll(jobRetry, downloadRetry); // both find nothing left to retry
        Assert.False(await Exists(failed));
    }

    [Fact]
    public async Task RetryRequestedWhileCleaningUp_KeepsTheJobInTheList()
    {
        var failed = Job("wants-retry", 31);
        var folder = CreateFolder("wants-retry");
        await Seed(failed, FinishedDownload(failed));
        _onDelete = _ => Change(failed, t => t.Retry = DateTimeOffset.UtcNow);

        Assert.False(await RemoveFailed(failed));
        Assert.True(await Exists(failed)); // so the retry can go ahead
        Assert.True(File.Exists(Path.Combine(folder, "synthetic.mkv")));
    }

    [Fact]
    public async Task ProviderFailure_KeepsJobAndFiles_AndBacksOff()
    {
        var failed = Job("pfail", 31);
        var folder = CreateFolder("pfail");
        await Seed(failed, FinishedDownload(failed));
        _onDelete = _ => throw new HttpRequestException("synthetic");

        await Tick(20);

        Assert.True(await Exists(failed));
        Assert.True(File.Exists(Path.Combine(folder, "synthetic.mkv")));
        VerifyProviderDeletes(1); // 20 passes, one attempt

        // Once it works again the job is cleaned up completely.
        _onDelete = _ => Task.CompletedTask;
        _runnerState.FailedRemovals.Clear();
        await Tick(1);

        Assert.False(await Exists(failed));
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task ProviderOutage_WithManyFailedJobs_MakesOneAttempt()
    {
        var jobs = Enumerable.Range(0, 20).Select(i => Job($"outage{i}", 600)).ToList();
        await Seed(jobs.Cast<Object>().ToArray());
        _listOverride = "unreadable";

        await Tick(20);

        Assert.Equal(20, await CountExisting(jobs));
        Assert.Equal(1, _lists);
        VerifyProviderDeletes(0);
    }

    [Fact]
    public async Task RateLimit_PausesEverything_AndKeepsTheJobs()
    {
        _onDelete = _ => throw new RateLimitException("slow down", TimeSpan.FromMinutes(20));
        var jobs = new[] { Job("rl1", 50), Job("rl2", 40) };
        await Seed(jobs.Cast<Object>().ToArray());

        await Tick(20);

        Assert.Equal(2, await CountExisting(jobs));
        Assert.True(_runnerState.FailedRemovals.NextAllowedAt > DateTimeOffset.UtcNow.AddMinutes(19));
        Assert.Equal(1, _lists);
        VerifyProviderDeletes(1);
    }

    [Fact]
    public async Task LongList_IsRemovedOneAtATime_WithAPause()
    {
        _runnerState.FailedRemovals.Interval = new FailedJobRemovalState().Interval; // the real one
        Assert.Equal(TimeSpan.FromSeconds(30), _runnerState.FailedRemovals.Interval);

        var jobs = Enumerable.Range(0, 20).Select(i => Job($"many{i}", 600)).ToList();
        await Seed(jobs.Cast<Object>().ToArray());

        await Tick(10);

        Assert.Equal(19, await CountExisting(jobs));
        Assert.Equal(1, _lists);
        VerifyProviderDeletes(1);
    }

    [Fact]
    public void Backoff_DoublesUpToAnHour()
    {
        var state = new FailedJobRemovalState();
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var waits = Enumerable.Range(0, 7).Select(_ => (state.Fail(id, now) - now).TotalMinutes).ToList();

        Assert.Equal([5, 10, 20, 40, 60, 60, 60], waits);
    }

    [Fact]
    public async Task ListDeleteFailure_KeepsTheJob_AndItsSharedThingsSafe()
    {
        // A real database failure for the first job. It stays listed, so it still owns the shared job and folder.
        var blocked = Job("twin", 40);
        var other = Job("twin", 35);
        other.Hash = "local-twin-2";
        var folder = CreateFolder("twin");
        await Seed(blocked, other, FinishedDownload(blocked), FinishedDownload(other));

        await using (var context = CreateContext())
        {
            await context.Database.ExecuteSqlRawAsync("CREATE TRIGGER block_delete BEFORE DELETE ON Torrents WHEN old.Hash = 'local-twin' BEGIN SELECT RAISE(ABORT, 'synthetic'); END;");
        }

        await Tick(1);
        _runnerState.FailedRemovals.NextAllowedAt = DateTimeOffset.MinValue; // the pause is over, the first job is still backing off
        await Tick(1);

        Assert.True(await Exists(blocked));
        Assert.False(await Exists(other)); // one failure does not stop the others
        Assert.True(File.Exists(Path.Combine(folder, "synthetic.mkv")));
        VerifyProviderDeletes(0);

        await using var verify = CreateContext();
        Assert.Equal(blocked.TorrentId, (await verify.Downloads.SingleAsync()).TorrentId); // rolled back, not half deleted
    }

    [Theory]
    [InlineData("77", "infohash")] // failed job by id, other job still stored by hash
    [InlineData("INFOHASH", "77")] // and the other way around
    public async Task OldAndNewTorBoxIdentifiers_AreRecognisedAsTheSameJob(String failedId, String otherId)
    {
        var failed = Job("ids", 31);
        failed.Type = DownloadType.Torrent;
        failed.Hash = "infohash";
        failed.RdId = failedId;

        var other = Job("ids-other", 0);
        other.Type = DownloadType.Torrent;
        other.Hash = "infohash";
        other.RdId = otherId;
        other.Error = null;

        _torBoxJobs[77] = (DownloadType.Torrent, "infohash");
        await Seed(failed, other);

        Assert.True(await RemoveFailed(failed));
        Assert.True(await Exists(other));
        VerifyProviderDeletes(0);
    }

    [Theory]
    [InlineData("77")]
    [InlineData("INFOHASH")]
    public async Task Torrent_IsDeletedByItsIdOrOldHash_AndNothingElseIs(String rdId)
    {
        var failed = Job("single", 31);
        failed.Type = DownloadType.Torrent;
        failed.Hash = "infohash";
        failed.RdId = rdId;

        _torBoxJobs[77] = (DownloadType.Torrent, "infohash");
        _torBoxJobs[78] = (DownloadType.Torrent, "somebody-elses");
        _torBoxJobs[79] = (DownloadType.Nzb, "infohash"); // same text, other kind of job
        await Seed(failed);

        Assert.True(await RemoveFailed(failed));
        Assert.Equal([77], _deleted);
    }

    [Fact]
    public async Task JobAlreadyGoneAtTorBox_LeavesTheList_WithoutADeleteRequest()
    {
        var failed = Job("gone", 31);
        await Seed(failed);
        _torBoxJobs.Clear();

        Assert.True(await RemoveFailed(failed));
        Assert.Equal(1, _lists);
        VerifyProviderDeletes(0);
    }

    [Theory]
    [InlineData("{\"success\":false}")]
    [InlineData("{\"success\":true,\"data\":[{\"id\":1}]}")] // a record that could be ours but has no hash
    [InlineData("{\"success\":true,\"data\":[{\"id\":1,\"hash\":\"provider-unsure\"},{\"id\":2,\"hash\":\"provider-unsure\"}]}")]
    public async Task UnclearTorBoxList_DeletesNothing(String json)
    {
        var failed = Job("unsure", 31);
        await Seed(failed);
        _listOverride = json;

        await Assert.ThrowsAnyAsync<Exception>(() => RemoveFailed(failed));

        Assert.True(await Exists(failed));
        VerifyProviderDeletes(0);
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("../movies")]
    public async Task ReleaseNameThatLeavesItsFolder_DeletesNoFiles(String name)
    {
        var tv = CreateFolder("innocent");
        var movies = Path.Combine(DownloadRoot, "movies");
        Directory.CreateDirectory(movies);

        foreach (var directory in new[] { DownloadRoot, Path.Combine(DownloadRoot, "tv"), movies })
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "synthetic.mkv"), "x");
        }

        var job = Job("escape", 31);
        job.RdName = name;
        await Seed(job, FinishedDownload(job));

        Assert.True(await RemoveFailed(job));

        Assert.True(File.Exists(Path.Combine(tv, "synthetic.mkv")));
        Assert.Equal(4, Directory.GetFiles(DownloadRoot, "synthetic.mkv", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public async Task OnlyFilesThisJobDownloaded_AreDeleted()
    {
        var job = Job("mixed", 31);
        var folder = CreateFolder("mixed");
        await File.WriteAllTextAsync(Path.Combine(folder, "not-ours.mkv"), "x");
        await Seed(job, FinishedDownload(job));

        Assert.True(await RemoveFailed(job));

        Assert.False(File.Exists(Path.Combine(folder, "synthetic.mkv")));
        Assert.True(File.Exists(Path.Combine(folder, "not-ours.mkv")));
    }

    [Fact]
    public async Task DownloadThatNeverStarted_DeletesNoFile()
    {
        var job = Job("unstarted", 31);
        var folder = CreateFolder("unstarted");
        var download = FinishedDownload(job);
        download.DownloadStarted = null;
        await Seed(job, download);

        Assert.True(await RemoveFailed(job));
        Assert.True(File.Exists(Path.Combine(folder, "synthetic.mkv")));
    }

    [Fact]
    public async Task SameNameInAnotherCategory_KeepsFiles()
    {
        // The good job was moved to another category. Its files are still in the old folder.
        var failed = Job("moved", 31);
        var good = Regrab("moved");
        good.RdId = "provider-moved-other";
        good.Category = "movies";
        var folder = CreateFolder("moved");
        await Seed(failed, good, FinishedDownload(failed));

        Assert.True(await RemoveFailed(failed));
        Assert.True(File.Exists(Path.Combine(folder, "synthetic.mkv")));
    }

    [Theory]
    [InlineData("show", "show/season")]
    [InlineData("show/season", "show")]
    public async Task ParentOrChildFolderOfAnotherJob_IsKept(String failedName, String otherName)
    {
        var folder = CreateFolder(failedName);
        var failed = Job("nested", 31);
        failed.RdName = failedName;
        var other = Job("nested-other", 0);
        other.RdName = otherName;
        other.Error = null;
        await Seed(failed, other, FinishedDownload(failed));

        Assert.True(await RemoveFailed(failed));
        Assert.True(File.Exists(Path.Combine(folder, "synthetic.mkv")));
    }

    [Fact]
    public async Task JobWithARunningLocalDownload_IsNotRemoved()
    {
        var job = Job("running", 31);
        var download = FinishedDownload(job);
        await Seed(job, download);
        _runnerState.ActiveDownloadClients[download.DownloadId] = new(download, job, DownloadRoot, "tv");

        Assert.False(await RemoveFailed(job));
        Assert.True(await Exists(job));
        VerifyProviderDeletes(0);
    }

    [Fact]
    public async Task ProviderJobOfAnotherProvider_IsNotDeleted()
    {
        var job = Job("foreign", 31);
        job.ClientKind = Provider.RealDebrid;
        await Seed(job);

        Assert.True(await RemoveFailed(job));

        Assert.False(await Exists(job));
        Assert.Equal(0, _lists);
        VerifyProviderDeletes(0);
    }
}
