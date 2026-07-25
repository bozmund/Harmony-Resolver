namespace Harmony.Resolver.Api.Infrastructure.Persistence.Entities;

public sealed class MetadataBackfillLeaseEntity
{
    public required string VideoId { get; set; }
    public Guid OwnerId { get; set; }
    public DateTimeOffset AcquiredAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public MetadataBackfillJobEntity? Job { get; set; }
}
