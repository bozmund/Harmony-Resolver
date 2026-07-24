using Harmony.Resolver.Api.Infrastructure.Metadata;
using Xunit;

namespace Harmony.Resolver.UnitTests;

public sealed class MetadataBackfillQueueTests
{
    [Fact]
    public void Same_id_is_not_enqueued_twice_within_the_cooldown()
    {
        var queue = new MetadataBackfillQueue(new MutableTimeProvider(DateTimeOffset.Parse("2026-07-25T00:00:00Z")));

        Assert.True(queue.TryEnqueue("dQw4w9WgXcQ"));
        Assert.False(queue.TryEnqueue("dQw4w9WgXcQ"));
    }

    [Fact]
    public void Id_is_retryable_once_the_cooldown_elapses()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-07-25T00:00:00Z"));
        var queue = new MetadataBackfillQueue(clock);
        Assert.True(queue.TryEnqueue("dQw4w9WgXcQ"));

        clock.Advance(MetadataBackfillQueue.Cooldown + TimeSpan.FromSeconds(1));

        Assert.True(queue.TryEnqueue("dQw4w9WgXcQ"));
    }

    [Fact]
    public void A_saturated_queue_drops_without_burning_the_cooldown()
    {
        // A single 100-id batch miss must not poison later attempts for ids it could not accept.
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-07-25T00:00:00Z"));
        var queue = new MetadataBackfillQueue(clock);
        var accepted = 0;
        for (var i = 0; i < 2000; i++)
            if (queue.TryEnqueue(VideoId(i)))
                accepted++;

        Assert.InRange(accepted, 1, 1999);
        var dropped = VideoId(1999);
        // Draining frees capacity; the dropped id must be immediately retryable, not cooling down.
        Assert.True(queue.Reader.TryRead(out _));
        Assert.True(queue.TryEnqueue(dropped));
    }

    [Fact]
    public void Prune_forgets_expired_cooldown_entries()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-07-25T00:00:00Z"));
        var queue = new MetadataBackfillQueue(clock);
        queue.TryEnqueue("dQw4w9WgXcQ");

        clock.Advance(MetadataBackfillQueue.Cooldown + TimeSpan.FromSeconds(1));
        queue.Prune();

        Assert.True(queue.TryEnqueue("dQw4w9WgXcQ"));
    }

    private static string VideoId(int index) => index.ToString("D11");

    private sealed class MutableTimeProvider(DateTimeOffset current) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan duration) => current += duration;
    }
}
