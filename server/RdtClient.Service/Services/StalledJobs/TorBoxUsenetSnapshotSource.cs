using RdtClient.Service.Services.DebridClients;
using TorBoxNET;

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
        var result = await torBoxDebridClient.GetUsenetSnapshot();

        return result?.Select(Map).ToList();
    }

    public static ProviderUsenetJob? Map(UsenetInfoResult? usenet)
    {
        if (usenet == null)
        {
            return null;
        }

        return new()
        {
            Hash = usenet.Hash,
            DownloadState = usenet.DownloadState,
            Progress = usenet.Progress,
            Active = usenet.Active,
            DownloadPresent = usenet.DownloadPresent,
            DownloadFinished = usenet.DownloadFinished,
            Size = usenet.Size,
            CreatedAt = usenet.CreatedAt == default ? null : usenet.CreatedAt,
            UpdatedAt = usenet.UpdatedAt == default ? null : usenet.UpdatedAt
        };
    }
}
