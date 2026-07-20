namespace Harmony.Resolver.Api.Configuration;

public enum ExtractionMode
{
    /// <summary>The server extracts audio itself inside the cache-miss request (dev/LAN default).</summary>
    Inline,
    /// <summary>The server never extracts; cache misses are queued for a credentialed downloader fleet.</summary>
    Delegated
}

public sealed class ResolverOptions
{
    public int MaxDurationMinutes { get; init; } = 9;
    public int MaxObjectMiB { get; init; } = 50;
    public TimeSpan ExtractionTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public long MaxMediaGiB { get; init; } = 50;
    public long PrefetchStopGiB { get; init; } = 45;
    public long BackupStopGiB { get; init; } = 48;
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(3);
    public bool UseFakeExtractor { get; init; }
    public ExtractionMode ExtractionMode { get; init; } = ExtractionMode.Inline;

    /// <summary>
    /// How long a track may sit <c>ingesting</c> before the stuck-job reaper fails it, so listeners
    /// polling a job no worker can complete eventually get a definitive error instead of polling forever.
    /// </summary>
    public TimeSpan JobMaxAge { get; init; } = TimeSpan.FromMinutes(10);
}
