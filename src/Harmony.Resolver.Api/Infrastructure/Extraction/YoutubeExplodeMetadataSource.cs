using Harmony.Resolver.Api.Abstractions;
using Harmony.Resolver.Api.Domain;
using YoutubeExplode;

namespace Harmony.Resolver.Api.Infrastructure.Extraction;

/// <summary>
/// Metadata via a single watch-page read. Notably does NOT touch the stream manifest, which is the
/// expensive and rate-limited part of extraction.
/// </summary>
public sealed class YoutubeExplodeMetadataSource(YoutubeClient youtube) : ITrackMetadataSource
{
    public async Task<TrackMetadata?> FetchAsync(string videoId, CancellationToken cancellationToken)
    {
        try
        {
            var video = await youtube.Videos.GetAsync(videoId, cancellationToken);
            if (string.IsNullOrWhiteSpace(video.Title)) return null;
            return new TrackMetadata(
                videoId,
                video.Title,
                string.IsNullOrWhiteSpace(video.Author.ChannelTitle) ? null : [video.Author.ChannelTitle],
                Album: null,
                DurationSeconds: video.Duration is { } duration ? (int)duration.TotalSeconds : null,
                ThumbnailUrl: video.Thumbnails.MaxBy(x => x.Resolution.Area)?.Url);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Backfill is best effort — a failure just means the next read tries again after the
            // cooldown, and the client's own resolution path covers the gap meanwhile.
            return null;
        }
    }
}
