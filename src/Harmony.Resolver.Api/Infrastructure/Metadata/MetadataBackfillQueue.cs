using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Harmony.Resolver.Api.Infrastructure.Metadata;

/// <summary>
/// Bounded, deduplicated work queue for lazy metadata fills. A single batch read can miss on 100
/// ids at once, so this drops rather than blocks when saturated and refuses to re-enqueue an id
/// that was attempted within <see cref="Cooldown"/> — a metadata miss must never turn into a
/// stampede against upstream.
/// </summary>
public sealed class MetadataBackfillQueue(TimeProvider clock)
{
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(10);
    private const int Capacity = 500;

    // Wait, not DropWrite: under DropWrite a full channel silently discards the item and still
    // reports success, which would burn the id's cooldown for work that never happened. Nothing
    // ever calls WriteAsync, so TryWrite simply returns false instead of ever blocking.
    private readonly Channel<string> _channel = Channel.CreateBounded<string>(
        new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.Wait });
    private readonly ConcurrentDictionary<string, DateTimeOffset> _attempted = new(StringComparer.Ordinal);

    public ChannelReader<string> Reader => _channel.Reader;

    /// Returns whether the id was accepted; false means duplicate, cooling down, or queue full.
    public bool TryEnqueue(string videoId)
    {
        var now = clock.GetUtcNow();
        if (_attempted.TryGetValue(videoId, out var last) && now - last < Cooldown) return false;
        _attempted[videoId] = now;
        if (_channel.Writer.TryWrite(videoId)) return true;
        // Dropped because the queue is full — allow a retry on the next read rather than holding
        // the cooldown against an id that never actually got attempted.
        _attempted.TryRemove(videoId, out _);
        return false;
    }

    /// Drops cooldown entries older than <see cref="Cooldown"/> so the map cannot grow unbounded.
    public void Prune()
    {
        var cutoff = clock.GetUtcNow() - Cooldown;
        foreach (var (videoId, attemptedAt) in _attempted)
            if (attemptedAt < cutoff)
                _attempted.TryRemove(videoId, out _);
    }
}
