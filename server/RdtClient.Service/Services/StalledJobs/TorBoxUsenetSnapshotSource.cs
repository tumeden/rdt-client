using System.Globalization;
using System.Text.Json;
using RdtClient.Service.Services.DebridClients;

namespace RdtClient.Service.Services.StalledJobs;

public interface IUsenetSnapshotSource
{
    /// <summary>
    ///     One request for the whole account. Returns null when the provider returned no usable payload.
    ///     Throws RateLimitException (with Retry-After) or other exceptions on failure.
    /// </summary>
    Task<IReadOnlyList<ProviderUsenetJob?>?> GetSnapshot();
}

public class TorBoxUsenetSnapshotSource(TorBoxDebridClient torBoxDebridClient) : IUsenetSnapshotSource
{
    public async Task<IReadOnlyList<ProviderUsenetJob?>?> GetSnapshot()
    {
        var json = await torBoxDebridClient.GetUsenetMyListJson();

        return Parse(json);
    }

    /// <summary>
    ///     Parses the raw usenet/mylist payload. Any field that is missing or has the wrong JSON type stays null,
    ///     which can never satisfy the stall condition (fail closed). Returns null when the envelope itself is not
    ///     a successful TorBox response with a data array.
    /// </summary>
    public static IReadOnlyList<ProviderUsenetJob?>? Parse(String? json)
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

            return data.EnumerateArray().Select(ParseJob).ToList();
        }
    }

    private static ProviderUsenetJob? ParseJob(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new()
        {
            Hash = GetString(element, "hash"),
            DownloadState = GetString(element, "download_state"),
            Progress = GetDouble(element, "progress"),
            Active = GetBool(element, "active"),
            DownloadPresent = GetBool(element, "download_present"),
            DownloadFinished = GetBool(element, "download_finished"),
            Size = GetDouble(element, "size") is { } size ? (Int64) size : null,
            CreatedAt = GetTimestamp(element, "created_at"),
            UpdatedAt = GetTimestamp(element, "updated_at")
        };
    }

    private static String? GetString(JsonElement element, String name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static Boolean? GetBool(JsonElement element, String name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static Double? GetDouble(JsonElement element, String name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : null;
    }

    private static DateTimeOffset? GetTimestamp(JsonElement element, String name)
    {
        var text = GetString(element, name);

        if (text == null)
        {
            return null;
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp) ? timestamp : null;
    }
}
