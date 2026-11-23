using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using RdtClient.Data.Enums;
using RdtClient.Data.Models.Internal;
using RdtClient.Service.Helpers;
using RdtClient.Service.Services;
using RdtClient.Service.Services.DebridClients;

namespace RdtClient.Service.Test.Services.StalledJobs;

/// <summary>
///     Uses the real DI HTTP client registrations (RegisterHttpClients) with a counting stub as the primary handler,
///     so no request leaves the process. Proves one watchdog check makes exactly one HTTP attempt, while the shared
///     TorBox client (used by the rest of the app) would retry.
/// </summary>
public class WatchdogHttpClientTest
{
    private sealed class CountingHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public Int32 Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);

            return Task.FromResult(respond());
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        public Int32 Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);

            throw new HttpRequestException("synthetic network failure");
        }
    }

    private static (IHttpClientFactory factory, IRateLimitCoordinator coordinator) Build(HttpMessageHandler handler)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRateLimitCoordinator, RateLimitCoordinator>();
        services.RegisterHttpClients();

        foreach (var name in new[] { DiConfig.TORBOX_WATCHDOG_CLIENT, DiConfig.TORBOX_CLIENT_SLOW })
        {
            services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => handler);
        }

        var provider = services.BuildServiceProvider();

        return (provider.GetRequiredService<IHttpClientFactory>(), provider.GetRequiredService<IRateLimitCoordinator>());
    }

    private static TorBoxDebridClient CreateClient(IHttpClientFactory factory, IRateLimitCoordinator coordinator)
    {
        var settings = new TestSettings(new DbSettings());
        settings.Current.Provider.Provider = Provider.TorBox;
        settings.Current.Provider.ApiKey = StalledJobs.Fixtures.DummyApiKey;

        return new(Mock.Of<ILogger<TorBoxDebridClient>>(), factory, Mock.Of<IDownloadableFileFilter>(), coordinator, settings);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task ErrorStatus_SingleAttempt(HttpStatusCode status)
    {
        var handler = new CountingHandler(() => new(status));
        var (factory, coordinator) = Build(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => CreateClient(factory, coordinator).GetUsenetMyListJson());

        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task RateLimited_SingleAttempt_SurfacesRetryAfter()
    {
        var handler = new CountingHandler(() =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new(TimeSpan.FromMinutes(90));

            return response;
        });

        var (factory, coordinator) = Build(handler);

        var ex = await Assert.ThrowsAsync<RateLimitException>(() => CreateClient(factory, coordinator).GetUsenetMyListJson());

        Assert.Equal(1, handler.Requests);
        Assert.Equal(TimeSpan.FromMinutes(90), ex.RetryAfter);
    }

    [Fact]
    public async Task NetworkFailure_SingleAttempt()
    {
        var handler = new ThrowingHandler();
        var (factory, coordinator) = Build(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => CreateClient(factory, coordinator).GetUsenetMyListJson());

        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task Contrast_SharedTorBoxClient_WouldRetry()
    {
        // Guards the test itself: the same harness does observe retries on the app's retrying client.
        var handler = new CountingHandler(() => new(HttpStatusCode.RequestTimeout));
        var (factory, _) = Build(handler);

        using var response = await factory.CreateClient(DiConfig.TORBOX_CLIENT_SLOW).GetAsync("https://api.torbox.app/v1/api/usenet/mylist");

        Assert.Equal(3, handler.Requests);
    }

    [Fact]
    public async Task Success_ReturnsBody_SingleAttempt()
    {
        var handler = new CountingHandler(() => new(HttpStatusCode.OK) { Content = new StringContent("""{"success":true,"data":[]}""") });
        var (factory, coordinator) = Build(handler);

        var json = await CreateClient(factory, coordinator).GetUsenetMyListJson();

        Assert.Contains("\"success\":true", json);
        Assert.Equal(1, handler.Requests);
    }
}
