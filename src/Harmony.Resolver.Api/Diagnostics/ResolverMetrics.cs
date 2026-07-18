using System.Diagnostics.Metrics;

namespace Harmony.Resolver.Api.Diagnostics;

public sealed class ResolverMetrics
{
    public const string MeterName = "Harmony.Resolver.Api";
    private static readonly Meter Meter = new(MeterName);

    public Histogram<double> AudioServeDuration { get; } = Meter.CreateHistogram<double>(
        "resolver.audio.serve.duration",
        unit: "s",
        description: "Duration of a successful audio serve, labeled by cache hit/miss.");

    public Counter<long> ExtractionFailures { get; } = Meter.CreateCounter<long>(
        "resolver.extraction.failures",
        description: "Count of failed extractions, labeled by failure code.");
}
