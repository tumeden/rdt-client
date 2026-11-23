using RdtClient.Data.Enums;
using RdtClient.Data.Models.Data;
using RdtClient.Service.Services.StalledJobs;

namespace RdtClient.Service.Test.Services.StalledJobs;

// SYNTHETIC FIXTURES. Every torrent, hash and provider record below is fabricated test data shaped after the
// incident description; none of it is a live observation and no real provider credentials are used.

internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow()
    {
        return Now;
    }

    public void Advance(TimeSpan by)
    {
        Now += by;
    }
}

internal sealed class InMemoryStateStore : IStalledJobStateStore
{
    public StalledJobState State { get; private set; } = new();
    public Int32 Saves { get; private set; }

    public StalledJobState Load()
    {
        // Round-trip through a copy, like a file would.
        return System.Text.Json.JsonSerializer.Deserialize<StalledJobState>(System.Text.Json.JsonSerializer.Serialize(State))!;
    }

    public void Save(StalledJobState state)
    {
        Saves++;
        State = System.Text.Json.JsonSerializer.Deserialize<StalledJobState>(System.Text.Json.JsonSerializer.Serialize(state))!;
    }
}

internal static class Fixtures
{
    public const String DummyApiKey = "dummy-noncredential-test-key";

    public static readonly DateTimeOffset T0 = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Submitted TorBox NZB that RDTClient maps to Downloading (processing, 100%).</summary>
    public static Torrent SubmittedNzb(String hash, DateTimeOffset? added = null)
    {
        return new()
        {
            TorrentId = Guid.NewGuid(),
            Hash = $"local-{hash}",
            RdId = hash,
            RdName = $"Synthetic.Show.S01E01.{hash}",
            Type = DownloadType.Nzb,
            ClientKind = Provider.TorBox,
            Added = added ?? T0.AddDays(-3),
            RdAdded = added ?? T0.AddDays(-3),
            RdStatus = TorrentStatus.Downloading,
            RdStatusRaw = "processing",
            RdProgress = 100,
            RdSpeed = 0,
            TorrentRetryAttempts = 2,
            DownloadRetryAttempts = 2,
            Downloads = []
        };
    }

    /// <summary>Local queue job never sent to the provider (RdId/RdAdded null, Queued), deliberately very old.</summary>
    public static Torrent UnsubmittedQueued(String name, DateTimeOffset? added = null)
    {
        return new()
        {
            TorrentId = Guid.NewGuid(),
            Hash = $"local-{name}",
            RdName = name,
            Type = DownloadType.Nzb,
            Added = added ?? T0.AddDays(-30),
            RdStatus = TorrentStatus.Queued,
            TorrentRetryAttempts = 2,
            Downloads = []
        };
    }

    /// <summary>The stuck provider shape from the incident: processing, 100%, finished, inactive, not present.</summary>
    public static ProviderUsenetJob StalledJob(String hash, DateTimeOffset? updatedAt = null)
    {
        return new()
        {
            Hash = hash,
            DownloadState = "processing",
            Progress = 1.0,
            Active = false,
            DownloadPresent = false,
            DownloadFinished = true,
            Size = 1_000_000,
            CreatedAt = T0.AddDays(-3),
            UpdatedAt = updatedAt
        };
    }
}
