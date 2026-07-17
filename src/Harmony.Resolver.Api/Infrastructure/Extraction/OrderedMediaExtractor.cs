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
                logger.LogWarning("Extractor {Extractor} failed with {FailureCode} ({Detail}); trying the next adapter.",
                    adapter.Name, exception.Code, exception.InnerException?.Message ?? "no detail");
            }
            catch (Exception exception)
            {
                lastFailure = new ExtractionException("adapter_unavailable", adapter.Name, exception);
                logger.LogWarning(exception, "Extractor {Extractor} threw an unexpected exception; trying the next adapter.",
                    adapter.Name);
            }
        }

        throw lastFailure ?? new ExtractionException("no_extractor_available", "resolver");
    }
}
