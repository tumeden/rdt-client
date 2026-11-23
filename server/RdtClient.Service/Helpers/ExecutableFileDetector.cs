using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace RdtClient.Service.Helpers;

/// <summary>
///     Detects executable / script payloads by file name. Used to block releases such as fake "episodes" that are
///     really Windows executables. Only names are inspected; nothing is opened or executed.
/// </summary>
public static partial class ExecutableFileDetector
{
    public static readonly IReadOnlySet<String> BlockedExtensions = new HashSet<String>(StringComparer.OrdinalIgnoreCase)
    {
        // ".com" is intentionally excluded: it collides with domain names in NZB subjects (e.g. "[www.site.com]").
        ".exe", ".scr", ".pif", ".bat", ".cmd", ".msi", ".msp", ".lnk",
        ".vbs", ".vbe", ".jse", ".wsf", ".wsh", ".ps1", ".hta", ".cpl"
    };

    /// <summary>Error text prefix used when a job is blocked, so it is recognisable in the UI and SAB/qBit history.</summary>
    public const String ErrorPrefix = "Blocked: contains executable files";

    public static Boolean IsExecutable(String? fileName)
    {
        if (String.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var name = fileName.Trim().TrimEnd('.', ' ');
        name = name[(name.LastIndexOfAny(['/', '\\']) + 1)..];

        if (BlockedExtensions.Contains(Path.GetExtension(name)))
        {
            return true;
        }

        // Split volumes of an executable, e.g. "setup.exe.001".
        var split = SplitVolumeRegex().Match(name);

        return split.Success && BlockedExtensions.Contains(Path.GetExtension(split.Groups["base"].Value));
    }

    public static IReadOnlyList<String> FindExecutables(IEnumerable<String?> fileNames)
    {
        return fileNames.Where(IsExecutable).Select(m => m!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    ///     File names referenced by an NZB, taken from each &lt;file subject="..."&gt;. Prefers the quoted name
    ///     ("name.ext") used by virtually all posters; otherwise falls back to name-like tokens in the subject.
    /// </summary>
    public static IReadOnlyList<String> GetNzbFileNames(Byte[] nzbBytes)
    {
        using var stream = new MemoryStream(nzbBytes);

        using var reader = XmlReader.Create(stream,
                                            new()
                                            {
                                                DtdProcessing = DtdProcessing.Ignore,
                                                XmlResolver = null
                                            });

        var doc = XDocument.Load(reader);
        var names = new List<String>();

        foreach (var file in doc.Descendants().Where(m => m.Name.LocalName == "file"))
        {
            var subject = file.Attribute("subject")?.Value;

            if (String.IsNullOrWhiteSpace(subject))
            {
                continue;
            }

            // Quoted names first, then any name-like tokens in the rest of the subject (a poster may quote one
            // harmless name and leave the payload unquoted).
            names.AddRange(QuotedNameRegex().Matches(subject).Select(m => m.Groups["name"].Value).Where(m => m.Contains('.')));

            var unquoted = QuotedNameRegex().Replace(subject, " ");
            names.AddRange(NameTokenRegex().Matches(unquoted).Select(m => m.Value));
        }

        return names;
    }

    /// <summary>Executables referenced by an NZB. Returns an empty list when the NZB cannot be parsed (fail open: the provider-side check still applies).</summary>
    public static IReadOnlyList<String> FindExecutablesInNzb(Byte[] nzbBytes)
    {
        try
        {
            return FindExecutables(GetNzbFileNames(nzbBytes));
        }
        catch (XmlException)
        {
            return [];
        }
    }

    public static String BuildError(IReadOnlyList<String> executables)
    {
        var shown = String.Join(", ", executables.Take(5));
        var more = executables.Count > 5 ? $" and {executables.Count - 5} more" : "";

        return $"{ErrorPrefix} ({shown}{more}). Not downloaded; nothing was executed.";
    }

    [GeneratedRegex("\"(?<name>[^\"]+)\"")]
    private static partial Regex QuotedNameRegex();

    [GeneratedRegex(@"[^\s""'<>|*?]+\.[A-Za-z0-9]{1,5}\b")]
    private static partial Regex NameTokenRegex();

    [GeneratedRegex(@"^(?<base>.+)\.\d{3}$")]
    private static partial Regex SplitVolumeRegex();
}
