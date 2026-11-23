using System.Text;
using RdtClient.Service.Helpers;

namespace RdtClient.Service.Test.Helpers;

// SYNTHETIC NZB snippets; no real releases.
public class ExecutableFileDetectorTest
{
    public static String Nzb(params String[] subjects)
    {
        var files = String.Join("\n", subjects.Select(s => $"""  <file poster="p@example.invalid" date="1700000000" subject="{System.Security.SecurityElement.Escape(s)}"><groups><group>alt.binaries.test</group></groups><segments><segment bytes="1" number="1">a@b</segment></segments></file>"""));

        return $"""
                <?xml version="1.0" encoding="utf-8"?>
                <!DOCTYPE nzb PUBLIC "-//newzBin//DTD NZB 1.1//EN" "http://www.newzbin.com/DTD/nzb/nzb-1.1.dtd">
                <nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">
                  <head><meta type="name">Synthetic.Show.S01E01</meta></head>
                {files}
                </nzb>
                """;
    }

    [Theory]
    [InlineData("Synthetic.Show.S01E01.exe", true)]
    [InlineData("Synthetic.Show.S01E01.mkv.exe", true)]
    [InlineData("folder/Setup.EXE", true)]
    [InlineData(@"folder\run.bat", true)]
    [InlineData("installer.msi", true)]
    [InlineData("shortcut.lnk", true)]
    [InlineData("payload.scr", true)]
    [InlineData("script.ps1", true)]
    [InlineData("setup.exe.001", true)]
    [InlineData("Synthetic.Show.S01E01.mkv", false)]
    [InlineData("Synthetic.Show.S01E01.part01.rar", false)]
    [InlineData("Synthetic.Show.S01E01.par2", false)]
    [InlineData("Synthetic.Show.S01E01.exe.vol00+01.par2", false)]
    [InlineData("Synthetic.Show.S01E01.nfo", false)]
    [InlineData("www.site.com", false)]
    [InlineData("subs.srt", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsExecutable(String? name, Boolean expected)
    {
        Assert.Equal(expected, ExecutableFileDetector.IsExecutable(name));
    }

    [Fact]
    public void Nzb_QuotedSubjects_FindsExecutable()
    {
        var nzb = Nzb("[1/3] - \"Synthetic.Show.S01E01.exe\" yEnc (1/50) 52428800",
                      "[2/3] - \"Synthetic.Show.S01E01.par2\" yEnc (1/1) 1024",
                      "[3/3] - \"Synthetic.Show.S01E01.vol00+01.par2\" yEnc (1/1) 2048");

        Assert.Equal(["Synthetic.Show.S01E01.exe"], ExecutableFileDetector.FindExecutablesInNzb(Encoding.UTF8.GetBytes(nzb)));
    }

    [Fact]
    public void Nzb_UnquotedSubject_FallsBackToTokens_WithoutDomainFalsePositive()
    {
        var nzb = Nzb("[www.site.com] Synthetic.Show.S01E01.exe yEnc (1/50)",
                      "[www.site.com] Synthetic.Show.S01E01.nfo yEnc (1/1)");

        Assert.Equal(["Synthetic.Show.S01E01.exe"], ExecutableFileDetector.FindExecutablesInNzb(Encoding.UTF8.GetBytes(nzb)));
    }

    [Fact]
    public void Nzb_NormalVideoRelease_IsClean()
    {
        var nzb = Nzb("[01/12] - \"Synthetic.Show.S01E01.1080p.mkv\" yEnc (1/900)",
                      "[02/12] - \"Synthetic.Show.S01E01.1080p.nfo\" yEnc (1/1)",
                      "[03/12] - \"Synthetic.Show.S01E01.1080p.par2\" yEnc (1/1)",
                      "visit www.example.com for more [04/12] \"Synthetic.Show.S01E01.1080p.vol0+1.par2\" yEnc");

        Assert.Empty(ExecutableFileDetector.FindExecutablesInNzb(Encoding.UTF8.GetBytes(nzb)));
    }

    [Fact]
    public void Nzb_ObfuscatedNames_AreNotFlagged_HereProviderCheckCovers()
    {
        var nzb = Nzb("[1/2] - \"a8f3c0d9e1b2.bin\" yEnc (1/50)", "[2/2] - \"a8f3c0d9e1b2.par2\" yEnc (1/1)");

        Assert.Empty(ExecutableFileDetector.FindExecutablesInNzb(Encoding.UTF8.GetBytes(nzb)));
    }

    [Fact]
    public void Nzb_Malformed_ReturnsEmpty()
    {
        Assert.Empty(ExecutableFileDetector.FindExecutablesInNzb(Encoding.UTF8.GetBytes("<nzb><file subject=")));
    }

    [Fact]
    public void BuildError_ListsNamesAndSaysNothingExecutedOrDeleted()
    {
        var error = ExecutableFileDetector.BuildError(["a.exe", "b.exe", "c.exe", "d.exe", "e.exe", "f.exe", "g.exe"]);

        Assert.StartsWith(ExecutableFileDetector.ErrorPrefix, error);
        Assert.Contains("a.exe", error);
        Assert.Contains("and 2 more", error);
        Assert.Contains("nothing was executed", error);
        Assert.DoesNotContain("deleted", error);
    }
}

public class ExecutableFileDetectorReviewFixesTest
{
    [Fact]
    public void Nzb_QuotedHarmlessName_PlusUnquotedExe_IsCaught()
    {
        var nzb = ExecutableFileDetectorTest.Nzb("\"Synthetic.Show.S01E05.nfo\" payload Synthetic.Show.S01E05.exe yEnc (1/50)");

        Assert.Equal(["Synthetic.Show.S01E05.exe"], RdtClient.Service.Helpers.ExecutableFileDetector.FindExecutablesInNzb(System.Text.Encoding.UTF8.GetBytes(nzb)));
    }

    [Theory]
    [InlineData("job/folder/file.mkv", "folder/file.mkv")]
    [InlineData("job/file.exe", "file.exe")]
    [InlineData("file.exe", "file.exe")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void TorBoxMapFilePath_KeepsSingleComponentNames(String? input, String expected)
    {
        Assert.Equal(expected, RdtClient.Service.Services.DebridClients.TorBoxDebridClient.MapFilePath(input));
    }
}
