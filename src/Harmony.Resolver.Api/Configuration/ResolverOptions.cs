namespace Harmony.Resolver.Api.Configuration;

public sealed class ResolverOptions
{
    public int MaxDurationMinutes { get; init; } = 15;
    public int MaxObjectMiB { get; init; } = 50;
    public TimeSpan ExtractionTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan InactivityExpiry { get; init; } = TimeSpan.FromDays(1);
    public bool UseFakeExtractor { get; init; }
}
