using System.Globalization;
using System.Text.Json;
using RdtClient.Data.Enums;

namespace RdtClient.Service.Services.TorBoxCleanup;

/// <summary>One TorBox job from a raw mylist payload. Missing/wrong-typed fields are null (fail closed).</summary>
public sealed record TorBoxJob
{
    public DownloadType Type { get; init; }
    public Int64? Id { get; init; }
    public String? Hash { get; init; }
    public String? Name { get; init; }
    public Boolean? Active { get; init; }
    public String? DownloadState { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }

    public String Key => $"{Type}:{Id}";
}

public sealed class OrphanCleanupState
{
    public DateTimeOffset? LastRunAt { get; set; }
    public DateTimeOffset? LastSnapshotAt { get; set; }
    public DateTimeOffset? BackoffUntil { get; set; }
    public Int32 ConsecutiveFailures { get; set; }
    public Dictionary<String, OrphanObservation> Observations { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class OrphanObservation
{
    public DownloadType Type { get; set; }
    public Int64 Id { get; set; }
    public String? Hash { get; set; }
    public String? Name { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public Int32 Count { get; set; }
    public DateTimeOffset? LastReportedAt { get; set; }
}

public static class TorBoxMyListParser
{
    /// <summary>Returns null when the envelope is not a successful TorBox response with a data array.</summary>
    public static IReadOnlyList<TorBoxJob?>? Parse(String? json, DownloadType type)
    {
        if (String.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("success", out var success)
                || success.ValueKind != JsonValueKind.True
                || !root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            return data.EnumerateArray().Select(e => ParseJob(e, type)).ToList();
        }
    }

    private static TorBoxJob? ParseJob(JsonElement e, DownloadType type)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        Int64? id = e.TryGetProperty("id", out var idValue) && idValue.ValueKind == JsonValueKind.Number && idValue.TryGetInt64(out var parsed) ? parsed : null;

        if (id == null)
        {
            return null;
        }

        return new()
        {
            Type = type,
            Id = id,
            Hash = GetString(e, "hash"),
            Name = GetString(e, "name"),
            Active = e.TryGetProperty("active", out var active) ? active.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null } : null,
            DownloadState = GetString(e, "download_state"),
            UpdatedAt = GetString(e, "updated_at") is { } t && DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ts) ? ts : null
        };
    }

    private static String? GetString(JsonElement e, String name)
    {
        return e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
