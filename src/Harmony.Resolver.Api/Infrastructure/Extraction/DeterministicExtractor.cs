using System.Text;
using Harmony.Resolver.Api.Abstractions;
using Harmony.Resolver.Api.Diagnostics;

namespace Harmony.Resolver.Api.Infrastructure.Extraction;

public sealed class DeterministicExtractor(FaultInjectionState? faults = null) : IMediaExtractor
{
    public async Task<byte[]> ExtractAsync(string videoId, CancellationToken cancellationToken)
    {
        if (faults?.Profile == "extractor-timeout") await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        if (faults?.Profile == "malformed-metadata") throw new InvalidDataException("Injected malformed metadata.");
        if (faults?.Profile == "partial-ffmpeg-output") throw new InvalidDataException("Injected partial FFmpeg output.");
        await Task.Delay(150, cancellationToken);
        // Deterministic Ogg-shaped fixture. Integration tests use this without contacting YouTube.
        return Encoding.UTF8.GetBytes("OggS\0HarmonyResolverFixture:" + videoId + new string('.', 4096));
    }
}
