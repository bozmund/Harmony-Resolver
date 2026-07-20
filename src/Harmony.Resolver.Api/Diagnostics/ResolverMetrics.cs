using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Harmony.Resolver.Api.Diagnostics;

public sealed class ResolverMetrics
{
    public const string MeterName = "Harmony.Resolver.Api";
    private static readonly Meter Meter = new(MeterName);

    public Histogram<double> AudioServeDuration { get; } = Meter.CreateHistogram<double>(
        "resolver.audio.serve.duration",
        unit: "s",
        description: "Full-transfer duration of a successful audio serve, labeled by cache hit/miss.");

    public Histogram<double> AudioFirstByteDuration { get; } = Meter.CreateHistogram<double>(
        "resolver.audio.first_byte.duration",
        unit: "s",
        description: "Request-to-first-response-write duration, labeled only by cache status and range position.");

    public Counter<long> ExtractionFailures { get; } = Meter.CreateCounter<long>(
        "resolver.extraction.failures",
        description: "Count of failed extractions, labeled by failure code.");

    internal Stream TrackFirstResponseWrite(
        Stream responseBody,
        Stopwatch requestStopwatch,
        AudioCacheStatus cacheStatus,
        AudioRangeKind rangeKind)
    {
        return new FirstWriteTrackingStream(responseBody, () =>
            AudioFirstByteDuration.Record(
                requestStopwatch.Elapsed.TotalSeconds,
                new KeyValuePair<string, object?>("cache", cacheStatus switch
                {
                    AudioCacheStatus.Hit => "hit",
                    AudioCacheStatus.Miss => "miss",
                    _ => throw new ArgumentOutOfRangeException(nameof(cacheStatus))
                }),
                new KeyValuePair<string, object?>("range", rangeKind switch
                {
                    AudioRangeKind.Initial => "initial",
                    AudioRangeKind.Nonzero => "nonzero",
                    _ => throw new ArgumentOutOfRangeException(nameof(rangeKind))
                })));
    }
}

internal enum AudioCacheStatus
{
    Hit,
    Miss
}

internal enum AudioRangeKind
{
    Initial,
    Nonzero
}
