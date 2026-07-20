namespace Harmony.Resolver.Api.Domain;

public sealed record StoredTrack(
    string VideoId,
    TrackStatus Status,
    string? ObjectKey,
    long? ContentLength,
    string? ETag,
    string? FailureCode,
    DateTimeOffset? RetryAfter,
    DateTimeOffset? ExpiresAt,
    IngestionPriority Priority = IngestionPriority.Urgent);
