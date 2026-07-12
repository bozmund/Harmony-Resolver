using Harmony.Resolver.Api.Abstractions;

namespace Harmony.Resolver.Api.Infrastructure.Extraction;

public sealed class OrderedMediaExtractor(IEnumerable<IExtractorAdapter> adapters, ILogger<OrderedMediaExtractor> logger)
    : IMediaExtractor
{
    public async Task<byte[]> ExtractAsync(string videoId, CancellationToken cancellationToken)
    {
        ExtractionException? lastFailure = null;
        foreach (var adapter in adapters)
        {
            try
            {
                return await adapter.ExtractAsync(videoId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ExtractionException exception)
            {
                lastFailure = exception;
                logger.LogWarning("Extractor {Extractor} failed with {FailureCode}; trying the next adapter.",
                    adapter.Name, exception.Code);
            }
        }

        throw lastFailure ?? new ExtractionException("no_extractor_available", "resolver");
    }
}
