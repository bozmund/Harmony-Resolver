using Harmony.Resolver.Api.Domain;

namespace Harmony.Resolver.Api.Abstractions;

public interface IExtractorAdapter
{
    string Name { get; }
    Task<ExtractedAudio> ExtractAsync(string videoId, CancellationToken cancellationToken);
}
