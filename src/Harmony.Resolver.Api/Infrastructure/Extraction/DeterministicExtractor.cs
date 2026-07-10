using System.Text;
using Harmony.Resolver.Api.Abstractions;

namespace Harmony.Resolver.Api.Infrastructure.Extraction;

public sealed class DeterministicExtractor : IMediaExtractor
{
    public async Task<byte[]> ExtractAsync(string videoId, CancellationToken cancellationToken)
    {
        await Task.Delay(150, cancellationToken);
        // Deterministic Ogg-shaped fixture. Integration tests use this without contacting YouTube.
        return Encoding.UTF8.GetBytes("OggS\0HarmonyResolverFixture:" + videoId + new string('.', 4096));
    }
}
