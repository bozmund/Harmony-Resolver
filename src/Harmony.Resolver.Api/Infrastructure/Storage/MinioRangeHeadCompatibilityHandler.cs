using System.Net;

namespace Harmony.Resolver.Api.Infrastructure.Storage;

/// <summary>
/// MinIO .NET 7 applies a requested GET byte range to its preliminary HEAD
/// existence check. MinIO correctly answers that ranged HEAD with 206, while
/// the SDK's StatObject path accepts only 200. Normalize only that internal
/// HEAD response; the following ranged GET remains a 206 response.
/// </summary>
internal sealed class MinioRangeHeadCompatibilityHandler(HttpMessageHandler innerHandler)
    : DelegatingHandler(innerHandler)
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        if (request.Method == HttpMethod.Head
            && request.Headers.Range is not null
            && response.StatusCode == HttpStatusCode.PartialContent)
        {
            response.StatusCode = HttpStatusCode.OK;
        }
        return response;
    }
}
