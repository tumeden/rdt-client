using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using RdtClient.Data.Enums;
using RdtClient.Service.Helpers;
using RdtClient.Service.Services;
using RdtClient.Service.Services.DebridClients;
using RdtClient.Service.Test.Services.StalledJobs;

namespace RdtClient.Service.Test.Services.TorBoxCleanup;

/// <summary>Real DI HTTP pipeline with a capturing stub handler; nothing leaves the process.</summary>
public class TorBoxDeleteRequestTest
{
    private sealed class CapturingHandler(HttpStatusCode status, String body) : HttpMessageHandler
    {
        public List<(HttpMethod method, String url, String? body, String? auth)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!.ToString(), request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken), request.Headers.Authorization?.Scheme));

            return new(status) { Content = new StringContent(body) };
        }
    }

    private static TorBoxDebridClient Create(CapturingHandler handler)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRateLimitCoordinator, RateLimitCoordinator>();
        services.RegisterHttpClients();
        services.AddHttpClient(DiConfig.TORBOX_WATCHDOG_CLIENT).ConfigurePrimaryHttpMessageHandler(() => handler);
        var provider = services.BuildServiceProvider();

        var settings = new TestSettings();
        settings.Current.Provider.ApiKey = Fixtures.DummyApiKey;

        return new(Mock.Of<ILogger<TorBoxDebridClient>>(), provider.GetRequiredService<IHttpClientFactory>(), Mock.Of<IDownloadableFileFilter>(), provider.GetRequiredService<IRateLimitCoordinator>(), settings);
    }

    [Theory]
    [InlineData(DownloadType.Nzb, "https://api.torbox.app/v1/api/usenet/controlusenetdownload", """{"usenet_id":42,"operation":"delete"}""")]
    [InlineData(DownloadType.Torrent, "https://api.torbox.app/v1/api/torrents/controltorrent", """{"torrent_id":42,"operation":"delete"}""")]
    public async Task Delete_SendsOneCorrectRequest(DownloadType type, String url, String body)
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, """{"success":true}""");

        await Create(handler).DeleteProviderJob(type, 42);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.method);
        Assert.Equal(url, request.url);
        Assert.Equal(body, request.body);
        Assert.Equal("Bearer", request.auth);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, """{"success":false,"error":"NOT_FOUND"}""")]
    [InlineData(HttpStatusCode.InternalServerError, "")]
    [InlineData(HttpStatusCode.RequestTimeout, "")]
    public async Task Delete_Failure_Throws_SingleAttempt(HttpStatusCode status, String body)
    {
        var handler = new CapturingHandler(status, body);

        await Assert.ThrowsAsync<HttpRequestException>(() => Create(handler).DeleteProviderJob(DownloadType.Nzb, 42));
        Assert.Single(handler.Requests);
    }
}
