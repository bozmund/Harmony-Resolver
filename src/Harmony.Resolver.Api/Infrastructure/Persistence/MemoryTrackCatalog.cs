using System.Collections.Concurrent;
using System.Security.Cryptography;
using Harmony.Resolver.Api.Abstractions;
using Harmony.Resolver.Api.Configuration;
using Harmony.Resolver.Api.Domain;

namespace Harmony.Resolver.Api.Infrastructure.Persistence;

public sealed class MemoryTrackCatalog : ITrackCatalog
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    public MemoryTrackCatalog()
    {
    }

    // Kept for source compatibility with older focused tests; retention is now permanent.
    public MemoryTrackCatalog(TimeProvider _, ResolverOptions __)
    {
    }

    public TrackInfo Get(string id) =>
        _entries.TryGetValue(id, out var e) ? e.Info : new TrackInfo(id, TrackStatus.Missing);

    public bool TryBegin(string id) => _entries.TryAdd(id, new Entry(new TrackInfo(id, TrackStatus.Ingesting), null));

    public void Ready(string id, byte[] audio)
    {
        var etag = '"' + Convert.ToHexString(SHA256.HashData(audio)).ToLowerInvariant() + '"';
        _entries[id] =
            new Entry(
                new TrackInfo(id, TrackStatus.Ready, audio.LongLength, etag), audio);
    }

    public void Failed(string id, string code) =>
        _entries[id] = new Entry(new TrackInfo(id, TrackStatus.Failed, FailureCode: code), null);

    public byte[]? Read(string id) => _entries.TryGetValue(id, out var e) ? e.Audio : null;
    public IReadOnlyCollection<TrackInfo> Snapshot() => _entries.Values.Select(x => x.Info).ToArray();

    private sealed record Entry(TrackInfo Info, byte[]? Audio);
}
