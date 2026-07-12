using Harmony.Resolver.Api.Abstractions;
using Harmony.Resolver.Api.Infrastructure.Extraction;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harmony.Resolver.UnitTests;

public sealed class OrderedMediaExtractorTests
{
    [Fact]
    public async Task Uses_yt_dlp_first_and_does_not_call_fallback_after_success()
    {
        var primary = new StubAdapter("yt-dlp", [1, 2, 3]);
        var fallback = new StubAdapter("youtube-explode", [4]);
        var extractor = new OrderedMediaExtractor([primary, fallback], NullLogger<OrderedMediaExtractor>.Instance);

        Assert.Equal([1, 2, 3], await extractor.ExtractAsync("dQw4w9WgXcQ", CancellationToken.None));
        Assert.Equal(1, primary.Calls);
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public async Task Falls_back_to_youtube_explode_after_yt_dlp_failure()
    {
        var primary = new StubAdapter("yt-dlp", new ExtractionException("yt_dlp_failed", "yt-dlp"));
        var fallback = new StubAdapter("youtube-explode", [4, 5]);
        var extractor = new OrderedMediaExtractor([primary, fallback], NullLogger<OrderedMediaExtractor>.Instance);

        Assert.Equal([4, 5], await extractor.ExtractAsync("dQw4w9WgXcQ", CancellationToken.None));
        Assert.Equal(1, primary.Calls);
        Assert.Equal(1, fallback.Calls);
    }

    private sealed class StubAdapter : IExtractorAdapter
    {
        private readonly byte[]? _result;
        private readonly Exception? _exception;
        public StubAdapter(string name, byte[] result) => (Name, _result) = (name, result);
        public StubAdapter(string name, Exception exception) => (Name, _exception) = (name, exception);
        public string Name { get; }
        public int Calls { get; private set; }
        public Task<byte[]> ExtractAsync(string videoId, CancellationToken cancellationToken)
        {
            Calls++;
            return _exception is null ? Task.FromResult(_result!) : Task.FromException<byte[]>(_exception);
        }
    }
}
