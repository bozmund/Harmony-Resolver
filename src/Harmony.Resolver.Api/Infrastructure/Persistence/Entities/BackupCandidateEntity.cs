namespace Harmony.Resolver.Api.Infrastructure.Persistence.Entities;

public sealed class BackupCandidateEntity
{
    public Guid Id { get; set; }
    public required string VideoId { get; set; }
    public required byte[] TokenHash { get; set; }
    public required string Status { get; set; }
    public string? StagingObjectKey { get; set; }
    public long? ContentLength { get; set; }
    public string? ETag { get; set; }
    public double? DurationSeconds { get; set; }
    public string? FingerprintA { get; set; }
    public string? FingerprintB { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
