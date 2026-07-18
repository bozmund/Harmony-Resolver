namespace Harmony.Resolver.Api.Infrastructure.Persistence.Entities;

public sealed class PlayEventEntity
{
    public long Id { get; set; }
    public required string VideoId { get; set; }
    public required string IdentityHash { get; set; }
    public required string Cache { get; set; }
    public long DurationMs { get; set; }
    public DateTimeOffset PlayedAt { get; set; }
}
