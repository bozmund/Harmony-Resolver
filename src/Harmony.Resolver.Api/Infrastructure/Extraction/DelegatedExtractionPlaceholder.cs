using Harmony.Resolver.Api.Abstractions;

namespace Harmony.Resolver.Api.Infrastructure.Extraction;

/// <summary>
/// Stands in for <see cref="IMediaExtractor"/> when <see cref="Configuration.ExtractionMode.Delegated"/>
/// is active. The cache-miss handler enqueues a job for the downloader fleet instead of extracting, so
/// this should never be invoked; it throws loudly if the delegated branch is ever bypassed.
/// </summary>
public sealed class DelegatedExtractionPlaceholder : IMediaExtractor
{
    public Task<byte[]> ExtractAsync(string videoId, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            "Extraction is delegated to the downloader fleet; the server must not extract in Delegated mode.");
}
