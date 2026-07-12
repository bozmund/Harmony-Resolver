namespace Harmony.Resolver.Api.Infrastructure.Persistence.Entities;

public sealed class IngestionLeaseEntity
{
    public required string VideoId { get; set; }
    public Guid OwnerId { get; set; }
    public DateTimeOffset AcquiredAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public TrackEntity Track { get; set; } = null!;
}
