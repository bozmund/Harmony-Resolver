namespace Harmony.Resolver.Api.Domain;

public sealed record AdminFailedTrack(
    string VideoId,
    string? Title,
    IReadOnlyList<string> Artists,
    string? FailureCode,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? RetryAfter);

public sealed record AdminFailedTrackPage(
    IReadOnlyList<AdminFailedTrack> Tracks,
    int Offset,
    int? NextOffset);

public enum AdminRetryOutcome
{
    Queued,
    NotFound,
    NotFailed,
    ActiveLease,
}

public sealed record AdminRetryResult(string VideoId, AdminRetryOutcome Outcome);
