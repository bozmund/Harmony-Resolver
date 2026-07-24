namespace Harmony.Resolver.Api.Infrastructure.Persistence.Entities;

/// <summary>
/// Display metadata, deliberately its own table rather than columns on <see cref="TrackEntity"/>.
/// Two reasons: a metadata-only row must never look like a pending download job to the fleet
/// (<c>ListPendingJobsAsync</c> selects on <c>status = 'ingesting'</c>), and metadata must outlive
/// the cached audio object that <c>ExpiredObjectJanitor</c> evicts.
/// </summary>
public sealed class TrackMetadataEntity
{
    public required string VideoId { get; set; }
    public string? Title { get; set; }
    /// JSON array of artist names, stored as jsonb.
    public string? ArtistsJson { get; set; }
    public string? Album { get; set; }
    public int? DurationSeconds { get; set; }
    public string? ThumbnailUrl { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
