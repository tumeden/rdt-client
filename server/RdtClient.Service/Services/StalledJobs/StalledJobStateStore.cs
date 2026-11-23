using System.Text.Json;
using Microsoft.Extensions.Logging;
using RdtClient.Data.Models.Internal;

namespace RdtClient.Service.Services.StalledJobs;

public interface IStalledJobStateStore
{
    StalledJobState Load();
    void Save(StalledJobState state);
}

/// <summary>
///     Persists watchdog state as JSON next to the SQLite database (e.g. /data/db/stalled-job-watchdog.json).
///     Writes are atomic (temp file + rename). An unreadable file is treated as empty state, which can only
///     delay failures (observation restarts), never cause them.
/// </summary>
public class StalledJobStateStore(ILogger<StalledJobStateStore> logger, AppSettings appSettings) : IStalledJobStateStore
{
    public const String FileName = "stalled-job-watchdog.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static readonly Object FileLock = new();

    public String FilePath
    {
        get
        {
            var dbPath = appSettings.Database?.Path;
            var directory = String.IsNullOrWhiteSpace(dbPath) ? null : Path.GetDirectoryName(Path.GetFullPath(dbPath));

            return Path.Combine(directory ?? AppContext.BaseDirectory, FileName);
        }
    }

    public StalledJobState Load()
    {
        lock (FileLock)
        {
            var path = FilePath;

            try
            {
                if (!File.Exists(path))
                {
                    return new();
                }

                var json = File.ReadAllText(path);
                var state = JsonSerializer.Deserialize<StalledJobState>(json, JsonOptions);

                if (state == null)
                {
                    return new();
                }

                state.Observations = new(state.Observations ?? new(), StringComparer.OrdinalIgnoreCase);

                return state;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Stall watchdog state at {Path} could not be read ({Message}); starting with empty observations", path, ex.Message);

                return new();
            }
        }
    }

    public void Save(StalledJobState state)
    {
        lock (FileLock)
        {
            var path = FilePath;
            var tempPath = path + ".tmp";

            File.WriteAllText(tempPath, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(tempPath, path, true);
        }
    }
}
