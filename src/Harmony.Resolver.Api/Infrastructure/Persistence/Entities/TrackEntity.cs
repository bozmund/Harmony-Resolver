namespace Harmony.Resolver.Api.Infrastructure.Persistence.Entities;

public sealed class TrackEntity
{
    public required string VideoId { get; set; }
    public required string Status { get; set; }
    public string? ObjectKey { get; set; }
    public long? ContentLength { get; set; }
    public string? ETag { get; set; }
    public string? FailureCode { get; set; }
    public DateTimeOffset? RetryAfter { get; set; }
    public DateTimeOffset LastAccessedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public IngestionLeaseEntity? Lease { get; set; }
}
