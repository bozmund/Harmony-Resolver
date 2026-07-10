namespace Harmony.Resolver.Api.Domain;

public sealed record TrackInfo(
    string VideoId,
    TrackStatus Status,
    long? Length = null,
    string? ETag = null,
    string? FailureCode = null,
    DateTimeOffset? ExpiresAt = null);
