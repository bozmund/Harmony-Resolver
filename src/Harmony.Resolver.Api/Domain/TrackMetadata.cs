namespace Harmony.Resolver.Api.Domain;

/// <summary>
/// Display metadata for a track, kept deliberately separate from <see cref="StoredTrack"/>: audio
/// caching and metadata have independent lifecycles. A track can be <c>ready</c> with no metadata
/// (ingested before this existed) or carry metadata with no cached audio (filled lazily on read).
/// </summary>
public sealed record TrackMetadata(
    string VideoId,
    string? Title = null,
    IReadOnlyList<string>? Artists = null,
    string? Album = null,
    int? DurationSeconds = null,
    string? ThumbnailUrl = null,
    DateTimeOffset? UpdatedAt = null)
{
    /// A row exists but was never populated — callers treat this the same as a miss.
    public bool IsEmpty => string.IsNullOrWhiteSpace(Title);
}
