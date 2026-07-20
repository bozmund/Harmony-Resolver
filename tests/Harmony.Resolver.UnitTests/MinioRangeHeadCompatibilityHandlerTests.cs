using System.Net;
using System.Net.Http.Headers;
using Harmony.Resolver.Api.Infrastructure.Storage;
using Xunit;

namespace Harmony.Resolver.UnitTests;

public sealed class MinioRangeHeadCompatibilityHandlerTests
{
    [Theory]
    [InlineData("HEAD", true, HttpStatusCode.PartialContent, HttpStatusCode.OK)]
    [InlineData("HEAD", false, HttpStatusCode.PartialContent, HttpStatusCode.PartialContent)]
    [InlineData("GET", true, HttpStatusCode.PartialContent, HttpStatusCode.PartialContent)]
    [InlineData("HEAD", true, HttpStatusCode.NotFound, HttpStatusCode.NotFound)]
    public async Task Only_ranged_head_partial_content_is_normalized(
        string method,
        bool hasRange,
        HttpStatusCode upstreamStatus,
        HttpStatusCode expectedStatus)
    {
        using var client = new HttpClient(
            new MinioRangeHeadCompatibilityHandler(
                new StubHandler(upstreamStatus)));
        using var request = new HttpRequestMessage(
            new HttpMethod(method),
            "http://minio.test/audio/track.ogg");
        if (hasRange)
            request.Headers.Range = new RangeHeaderValue(4_194_304, 4_259_839);

        using var response = await client.SendAsync(request);

        Assert.Equal(expectedStatus, response.StatusCode);
    }

    private sealed class StubHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                RequestMessage = request
            });
        }
    }
}
