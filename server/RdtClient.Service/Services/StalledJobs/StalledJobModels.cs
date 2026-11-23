namespace RdtClient.Service.Services.StalledJobs;

/// <summary>
///     Provider-side view of one TorBox Usenet job, taken from a single shared snapshot request.
///     Only the fields the watchdog needs; nullable where the provider payload may omit or garble them.
/// </summary>
public sealed record ProviderUsenetJob
{
    public String? Hash { get; init; }
    public String? DownloadState { get; init; }
    public Double? Progress { get; init; }
    public Boolean? Active { get; init; }
    public Boolean? DownloadPresent { get; init; }
    public Boolean? DownloadFinished { get; init; }
    public Int64? Size { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>
///     Persisted watchdog state. Survives restarts so observed stall time is not lost, and so a restart
///     cannot cause an immediate catch-up run.
/// </summary>
public sealed class StalledJobState
{
    public Int32 Version { get; set; } = 1;

    /// <summary>Last time a run was attempted (successful or not). Drives the hourly cadence.</summary>
    public DateTimeOffset? LastRunAt { get; set; }

    /// <summary>Last time a provider snapshot was fetched and evaluated successfully. Drives continuity checks.</summary>
    public DateTimeOffset? LastSnapshotAt { get; set; }

    /// <summary>No provider request is made before this time (Retry-After / error backoff).</summary>
    public DateTimeOffset? BackoffUntil { get; set; }

    public Int32 ConsecutiveFailures { get; set; }

    /// <summary>Keyed by local TorrentId.</summary>
    public Dictionary<String, StalledJobObservation> Observations { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class StalledJobObservation
{
    public String RdId { get; set; } = "";
    public String Fingerprint { get; set; } = "";

    /// <summary>First watchdog observation of the current unchanged qualifying condition.</summary>
    public DateTimeOffset FirstObservedAt { get; set; }

    public DateTimeOffset LastObservedAt { get; set; }
    public Int32 ObservationCount { get; set; }
    public DateTimeOffset? ProviderUpdatedAt { get; set; }
}

public sealed record StallDecision(Guid TorrentId, String RdId, String Reason, StalledJobObservation Observation);

public sealed record StalledJobEvaluation(StalledJobState State, IReadOnlyList<StallDecision> ToFail, StalledJobEvaluationStats Stats);

public sealed record StalledJobEvaluationStats
{
    public Int32 Candidates { get; init; }
    public Int32 Matched { get; init; }
    public Int32 MissingFromProvider { get; init; }
    public Int32 Ambiguous { get; init; }
    public Int32 Qualifying { get; init; }
    public Int32 Deferred { get; init; }
    public Int32 MalformedProviderEntries { get; init; }
    public String? ContinuityReset { get; init; }
}
