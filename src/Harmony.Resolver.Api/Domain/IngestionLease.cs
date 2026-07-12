namespace Harmony.Resolver.Api.Domain;

public sealed record IngestionLease(string VideoId, Guid OwnerId, DateTimeOffset ExpiresAt);
