namespace Harmony.Resolver.Api.Abstractions;

public interface IExtractorAdapter
{
    string Name { get; }
    Task<byte[]> ExtractAsync(string videoId, CancellationToken cancellationToken);
}
