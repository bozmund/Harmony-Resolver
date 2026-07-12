namespace Harmony.Resolver.Api.Configuration;

public sealed class QuotaOptions
{
    public int AnonymousIngestionsPerHour { get; init; } = 10;
    public int AuthenticatedIngestionsPerHour { get; init; } = 100;
    public int AnonymousConcurrentResponses { get; init; } = 2;
    public int AuthenticatedConcurrentResponses { get; init; } = 5;
    public string IdentityHmacKey { get; init; } = "development-only-change-me";
}
