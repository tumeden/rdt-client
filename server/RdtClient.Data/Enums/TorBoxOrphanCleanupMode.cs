using System.ComponentModel;

namespace RdtClient.Data.Enums;

public enum TorBoxOrphanCleanupMode
{
    [Description("Off")]
    Off = 0,

    [Description("Report only (log what would be removed)")]
    ReportOnly = 1,

    [Description("Remove from TorBox")]
    Delete = 2
}
