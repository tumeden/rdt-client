using System.ComponentModel;

namespace RdtClient.Data.Enums;

public enum ExecutableDownloadAction
{
    [Description("Fail the whole job")]
    FailJob = 0,

    [Description("Skip only the executable files")]
    SkipExecutableFiles = 1
}
