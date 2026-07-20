using System.Net;
using Harmony.Resolver.Downloader;
using Xunit;

namespace Harmony.Resolver.UnitTests;

public sealed class ResolverWorkerClientTests
{
    [Fact]
    public async Task Worker_routes_preserve_the_resolver_gateway_prefix()
    {
        var handler = new CaptureHandler();
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://harmony-api.example/resolver/")
        };
        var options = new DownloaderOptions
        {
            ResolverBaseUrl = http.BaseAddress.ToString()
        };
        var client = new ResolverWorkerClient(
            http, new Auth0TokenProvider(new HttpClient(handler), options));

        Assert.Null(await client.ClaimAsync(CancellationToken.None));
        Assert.Equal(
            "https://harmony-api.example/resolver/v1/worker/jobs/claim",
            handler.RequestUri?.ToString());
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }
}
