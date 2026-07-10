using Harmony.Resolver.Api;
using Xunit;

namespace Harmony.Resolver.UnitTests;

public sealed class VideoIdTests
{
    [Theory]
    [InlineData("dQw4w9WgXcQ", true)]
    [InlineData("../etc/passwd", false)]
    [InlineData("https://youtu.be/x", false)]
    [InlineData("too-short", false)]
    public void Validation_rejects_urls_and_unsafe_input(string value, bool expected) => Assert.Equal(expected, VideoIds.IsValid(value));

    [Fact]
    public void Exactly_one_caller_acquires_ingestion_lease()
    {
        var catalog = new MemoryTrackCatalog(TimeProvider.System, new ResolverOptions());
        var winners = Enumerable.Range(0, 20).AsParallel().Count(_ => catalog.TryBegin("dQw4w9WgXcQ"));
        Assert.Equal(1, winners);
    }
}