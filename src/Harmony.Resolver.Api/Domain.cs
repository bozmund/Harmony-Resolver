using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Harmony.Resolver.Api;

public enum TrackStatus { Missing, Ingesting, Ready, Failed }
public sealed record TrackInfo(string VideoId, TrackStatus Status, long? Length = null, string? ETag = null, string? FailureCode = null, DateTimeOffset? ExpiresAt = null);

public static partial class VideoIds
{
    [GeneratedRegex("^[A-Za-z0-9_-]{11}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
    public static bool IsValid(string value) => Pattern().IsMatch(value);
}

public interface ITrackCatalog
{
    TrackInfo Get(string videoId);
    bool TryBegin(string videoId);
    void Ready(string videoId, byte[] audio);
    void Failed(string videoId, string code);
    byte[]? Read(string videoId);
    IReadOnlyCollection<TrackInfo> Snapshot();
}

public sealed class MemoryTrackCatalog(TimeProvider clock, ResolverOptions options) : ITrackCatalog
{
    private readonly ConcurrentDictionary<string, Entry> entries = new();
    public TrackInfo Get(string id) => entries.TryGetValue(id, out var e) ? e.Info : new(id, TrackStatus.Missing);
    public bool TryBegin(string id) => entries.TryAdd(id, new(new(id, TrackStatus.Ingesting), null));
    public void Ready(string id, byte[] audio)
    {
        var etag = '"' + Convert.ToHexString(SHA256.HashData(audio)).ToLowerInvariant() + '"';
        entries[id] = new(new(id, TrackStatus.Ready, audio.LongLength, etag, ExpiresAt: clock.GetUtcNow() + options.InactivityExpiry), audio);
    }
    public void Failed(string id, string code) => entries[id] = new(new(id, TrackStatus.Failed, FailureCode: code), null);
    public byte[]? Read(string id) => entries.TryGetValue(id, out var e) ? e.Audio : null;
    public IReadOnlyCollection<TrackInfo> Snapshot() => entries.Values.Select(x => x.Info).ToArray();
    private sealed record Entry(TrackInfo Info, byte[]? Audio);
}

public sealed class ResolverOptions
{
    public int MaxDurationMinutes { get; init; } = 15;
    public int MaxObjectMiB { get; init; } = 50;
    public TimeSpan ExtractionTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan InactivityExpiry { get; init; } = TimeSpan.FromDays(1);
    public bool UseFakeExtractor { get; init; }
}

public interface IMediaExtractor { Task<byte[]> ExtractAsync(string videoId, CancellationToken cancellationToken); }

public sealed class DeterministicExtractor : IMediaExtractor
{
    public async Task<byte[]> ExtractAsync(string videoId, CancellationToken cancellationToken)
    {
        await Task.Delay(150, cancellationToken);
        // Deterministic Ogg-shaped fixture. Integration tests use this without contacting YouTube.
        return Encoding.UTF8.GetBytes("OggS\0HarmonyResolverFixture:" + videoId + new string('.', 4096));
    }
}

public sealed class ResolverDiagnostics(ITrackCatalog catalog)
{
    public object Snapshot() => new { generatedAt = DateTimeOffset.UtcNow, tracks = catalog.Snapshot(), counts = catalog.Snapshot().GroupBy(x => x.Status).ToDictionary(x => x.Key.ToString().ToLowerInvariant(), x => x.Count()) };
}
