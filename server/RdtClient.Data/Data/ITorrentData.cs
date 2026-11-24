using RdtClient.Data.Enums;
using RdtClient.Data.Models.Data;

namespace RdtClient.Data.Data;

public interface ITorrentData
{
    Task<IList<Torrent>> Get();
    Task<Torrent?> GetById(Guid torrentId);
    Task<Torrent?> GetByHash(String hash);
    Task<String?> GetPayloadContent(Guid torrentId);

    Task<Torrent> Add(String? rdId,
                      String hash,
                      String? fileOrMagnetContents,
                      Boolean isFile,
                      DownloadType downloadType,
                      DownloadClient downloadClient,
                      Torrent torrent);

    Task UpdateRdData(Torrent torrent);
    Task UpdateRdId(Torrent torrent, String rdId);
    Task UpdateHash(Torrent torrent, String hash);
    Task Update(Torrent torrent);
    Task UpdateCategory(Guid torrentId, String? category);
    Task UpdateComplete(Guid torrentId, String? error, DateTimeOffset? datetime, Boolean retry);
    Task UpdateFilesSelected(Guid torrentId, DateTimeOffset datetime);
    Task UpdatePriority(Guid torrentId, Int32? priority);
    Task UpdateRetry(Guid torrentId, DateTimeOffset? dateTime, Int32 retryCount);
    Task UpdateError(Guid torrentId, String error);

    /// <summary>
    ///     Atomically marks a stalled, submitted, idle job as failed (Completed + Error, retries exhausted, no retry)
    ///     with a single conditional UPDATE. Returns false and changes nothing if any guard no longer holds.
    /// </summary>
    Task<Boolean> TryMarkStalledFailed(Guid torrentId, String expectedRdId, String error, DateTimeOffset completed);
    Task Delete(Guid torrentId);

    /// <summary>
    ///     Atomically deletes a failed job, only if it is still failed, not being retried, unchanged since it was
    ///     read, and stillWanted returns true right before the commit.
    ///     Returns false and deletes nothing otherwise.
    /// </summary>
    Task<Boolean> TryDeleteFailed(Torrent expected, Func<Boolean> stillWanted);
}
