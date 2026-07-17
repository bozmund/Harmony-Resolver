using System.Text;
using Harmony.Resolver.Api.Infrastructure.Extraction;
using Xunit;

namespace Harmony.Resolver.UnitTests;

public sealed class DeterministicExtractorTests
{
    [Fact]
    public async Task Fixture_is_an_ogg_opus_stream()
    {
        var bytes = await new DeterministicExtractor().ExtractAsync("dQw4w9WgXcQ", CancellationToken.None);

        Assert.Equal("OggS", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Contains("OpusHead", Encoding.ASCII.GetString(bytes));
        Assert.True(bytes.Length > 1000);
    }
}
