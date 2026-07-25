namespace Harmony.Resolver.Api.Infrastructure.Persistence.Entities;

/// <summary>Durable metadata-only work. It intentionally has no effect on the audio track state.</summary>
public sealed class MetadataBackfillJobEntity
{
    public required string VideoId { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset? RetryAfter { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public MetadataBackfillLeaseEntity? Lease { get; set; }
}
