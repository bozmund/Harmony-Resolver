using Harmony.Resolver.Api.Domain;

namespace Harmony.Resolver.Api.Abstractions;

public interface IMediaExtractor
{
    Task<ExtractedAudio> ExtractAsync(string videoId, CancellationToken cancellationToken);
}
