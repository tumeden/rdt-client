using RdtClient.Service.Services.StalledJobs;

namespace RdtClient.Service.Test.Services.StalledJobs;

// SYNTHETIC payloads shaped like TorBox usenet/mylist responses.
public class TorBoxUsenetSnapshotParseTest
{
    private const String FullStalled = """
        {"hash":"h1","download_state":"processing","progress":1,"active":false,"download_present":false,
         "download_finished":true,"size":123,"created_at":"2026-11-17T10:00:00Z","updated_at":"2026-11-17T11:00:00Z"}
        """;

    private static String Envelope(String items)
    {
        return $$"""{"success":true,"error":null,"detail":"ok","data":[{{items}}]}""";
    }

    [Fact]
    public void FullRecord_ParsesAndQualifies()
    {
        var job = Assert.Single(TorBoxUsenetSnapshotSource.Parse(Envelope(FullStalled))!)!;

        Assert.Equal("h1", job.Hash);
        Assert.Equal(false, job.Active);
        Assert.Equal(false, job.DownloadPresent);
        Assert.Equal(true, job.DownloadFinished);
        Assert.Equal(new DateTimeOffset(2026, 11, 17, 11, 0, 0, TimeSpan.Zero), job.UpdatedAt);
        Assert.True(StalledJobEvaluator.IsStalledCondition(job));
    }

    [Theory]
    [InlineData("active")]
    [InlineData("download_present")]
    [InlineData("download_finished")]
    [InlineData("download_state")]
    [InlineData("progress")]
    public void OmittedField_IsNull_AndNeverQualifies(String field)
    {
        var item = System.Text.RegularExpressions.Regex.Replace(FullStalled, $"\"{field}\":[^,}}]+,?", "");
        var job = Assert.Single(TorBoxUsenetSnapshotSource.Parse(Envelope(item))!)!;

        Assert.False(StalledJobEvaluator.IsStalledCondition(job), field);
    }

    [Theory]
    [InlineData("\"active\":false", "\"active\":null")]
    [InlineData("\"active\":false", "\"active\":\"false\"")]
    [InlineData("\"active\":false", "\"active\":0")]
    [InlineData("\"download_present\":false", "\"download_present\":null")]
    [InlineData("\"download_present\":false", "\"download_present\":\"no\"")]
    [InlineData("\"progress\":1", "\"progress\":\"1\"")]
    public void WrongTypedField_IsNull_AndNeverQualifies(String original, String replacement)
    {
        var job = Assert.Single(TorBoxUsenetSnapshotSource.Parse(Envelope(FullStalled.Replace(original, replacement)))!)!;

        Assert.False(StalledJobEvaluator.IsStalledCondition(job));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"success":false,"error":"AUTH_ERROR","data":null}""")]
    [InlineData("""{"data":[]}""")]
    [InlineData("""{"success":true,"data":{}}""")]
    [InlineData("""{"success":true}""")]
    public void BadEnvelope_ReturnsNull(String json)
    {
        Assert.Null(TorBoxUsenetSnapshotSource.Parse(json));
    }

    [Fact]
    public void NonObjectItems_BecomeNullEntries()
    {
        var result = TorBoxUsenetSnapshotSource.Parse(Envelope($"1,\"x\",null,{FullStalled}"))!;

        Assert.Equal(4, result.Count);
        Assert.Equal(3, result.Count(m => m == null));
    }
}
