using Harmony.Resolver.Api.Domain;

namespace Harmony.Resolver.Api.Abstractions;

/// <summary>
/// Fetches display metadata for a video from upstream. Deliberately separate from
/// <see cref="IMediaExtractor"/>: this is a cheap metadata read, not an audio extraction, and in
/// delegated mode the fleet — not the API — is the component allowed to perform it.
/// </summary>
public interface ITrackMetadataSource
{
    Task<TrackMetadata?> FetchAsync(string videoId, CancellationToken cancellationToken);
}
