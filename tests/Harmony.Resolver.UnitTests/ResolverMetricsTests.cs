using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Harmony.Resolver.Api.Diagnostics;
using Xunit;

namespace Harmony.Resolver.UnitTests;

public sealed class ResolverMetricsTests
{
    [Fact]
    public async Task First_byte_metric_records_once_after_first_successful_write()
    {
        var samples = new ConcurrentQueue<MetricSample>();
        using var listener = ListenForFirstByte(samples);
        var metrics = new ResolverMetrics();
        var destination = new FailFirstWriteStream();
        var stopwatch = Stopwatch.StartNew();
        var responseBody = metrics.TrackFirstResponseWrite(
            destination,
            stopwatch,
            AudioCacheStatus.Hit,
            AudioRangeKind.Nonzero);

        await responseBody.WriteAsync(ReadOnlyMemory<byte>.Empty);
        await Assert.ThrowsAsync<IOException>(async () =>
            await responseBody.WriteAsync(new byte[] { 1 }));
        await responseBody.WriteAsync(new byte[] { 2 });
        await responseBody.WriteAsync(new byte[] { 3 });

        var sample = Assert.Single(samples);
        Assert.True(sample.DurationSeconds >= 0);
        Assert.Equal("hit", sample.Cache);
        Assert.Equal("nonzero", sample.Range);
    }

    [Fact]
    public async Task Disposing_tracker_does_not_close_aspnet_owned_response_body()
    {
        var metrics = new ResolverMetrics();
        await using var destination = new MemoryStream();
        var responseBody = metrics.TrackFirstResponseWrite(
            destination,
            Stopwatch.StartNew(),
            AudioCacheStatus.Miss,
            AudioRangeKind.Initial);

        await responseBody.DisposeAsync();
        await destination.WriteAsync(new byte[] { 1 });

        Assert.Equal(new byte[] { 1 }, destination.ToArray());
    }

    private static MeterListener ListenForFirstByte(ConcurrentQueue<MetricSample> samples)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, currentListener) =>
        {
            if (instrument.Meter.Name == ResolverMetrics.MeterName
                && instrument.Name == "resolver.audio.first_byte.duration")
            {
                currentListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, measurement, tags, _) =>
        {
            string? cache = null;
            string? range = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "cache") cache = tag.Value as string;
                if (tag.Key == "range") range = tag.Value as string;
            }
            samples.Enqueue(new MetricSample(measurement, cache, range));
        });
        listener.Start();
        return listener;
    }

    private sealed class FailFirstWriteStream : MemoryStream
    {
        private bool _fail = true;

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_fail && !buffer.IsEmpty)
            {
                _fail = false;
                return ValueTask.FromException(new IOException("Simulated disconnect before first byte."));
            }
            return base.WriteAsync(buffer, cancellationToken);
        }
    }

    private sealed record MetricSample(
        double DurationSeconds,
        string? Cache,
        string? Range);
}
