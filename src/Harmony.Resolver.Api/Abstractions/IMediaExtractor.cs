namespace Harmony.Resolver.Api.Abstractions;

public interface IMediaExtractor
{
    Task<byte[]> ExtractAsync(string videoId, CancellationToken cancellationToken);
}
