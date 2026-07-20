using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Harmony.Resolver.Api.Abstractions;
using Harmony.Resolver.Api.Diagnostics;
using Harmony.Resolver.Api.Domain;
using Harmony.Resolver.Api.Infrastructure.Extraction;
using Harmony.Resolver.Api.Infrastructure.Persistence;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;
using Xunit;

namespace Harmony.Resolver.IntegrationTests;

/// <summary>
/// Validates that a client disconnect mid-stream does NOT cancel the ingestion.
/// The leader continues tee-ing to the object store and marks the track Ready.
/// Uses real PostgreSQL + a fake object store to avoid needing real MinIO.
/// </summary>
public sealed class DisconnectContinuationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("harmony_test_disconnect")
        .WithUsername("harmony")
        .WithPassword("test-only-password")
        .Build();

    private WebApplicationFactory<Program> _factory = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        await using var db = new ResolverDbContext(
            new DbContextOptionsBuilder<ResolverDbContext>()
                .UseNpgsql(_postgres.GetConnectionString())
                .Options);
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Basic_extraction_roundtrip()
    {
        // Verify the distributed endpoint path works at all before testing disconnect.
        const string videoId = "AbCdEfGhIjK";
        var fakeStore = new FakeObjectStore();

        _factory = CreateFactory(fakeStore, new SlowDeterministicExtractor(TimeSpan.FromMilliseconds(100)));

        using var client = _factory.CreateClient();
        await WaitForReadyAsync(client);

        var response = await client.GetAsync($"/v1/tracks/{videoId}/audio");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        var repository = _factory.Services.GetRequiredService<ITrackRepository>();
        var track = await PollTrackAsync(repository, videoId, TimeSpan.FromSeconds(10));
        Assert.NotNull(track);
        Assert.Equal(TrackStatus.Ready, track.Status);
        Assert.NotNull(fakeStore.CapturedBytes);
    }

    [Fact]
    public async Task Client_disconnect_does_not_cancel_ingestion()
    {
        const string videoId = "DscnnctTst9";
        var fakeStore = new FakeObjectStore();
        var extractor = new SlowDeterministicExtractor(TimeSpan.FromSeconds(3));

        _factory = CreateFactory(fakeStore, extractor);

        using var warmup = _factory.CreateClient();
        await WaitForReadyAsync(warmup);

        using var client = _factory.CreateClient();

        try
        {
            var response = await client.GetAsync($"/v1/tracks/{videoId}/audio",
                HttpCompletionOption.ResponseHeadersRead);

            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

            using var body = await response.Content.ReadAsStreamAsync();
            var firstChunk = new byte[32];
            var read = await body.ReadAsync(firstChunk.AsMemory(0, 32));
            Assert.True(read > 0, "Response should be streaming");

            // Disconnect — let the body/client go out of scope
        }
        catch (OperationCanceledException) { }
        catch (HttpIOException) { }
        catch (IOException) { }

        var repository = _factory.Services.GetRequiredService<ITrackRepository>();
        var track = await PollTrackAsync(repository, videoId, TimeSpan.FromSeconds(25));
        Assert.NotNull(track);
        Assert.Equal(TrackStatus.Ready, track.Status);
        Assert.True(track.ContentLength > 0,
            "Track should be stored despite disconnect.");
        Assert.NotNull(fakeStore.CapturedBytes);
        Assert.True(fakeStore.CapturedBytes.Length > 0,
            "Object store should have the data.");
    }

    [Fact]
    public async Task Ready_track_serves_exact_initial_and_deep_ranges()
    {
        const string videoId = "RangeTst01A";
        var fakeStore = new FakeObjectStore();
        _factory = CreateFactory(
            fakeStore,
            new SlowDeterministicExtractor(TimeSpan.FromMilliseconds(10)));

        using var client = _factory.CreateClient();
        await WaitForReadyAsync(client);

        using var initialResponse = await client.GetAsync($"/v1/tracks/{videoId}/audio");
        Assert.Equal(HttpStatusCode.OK, initialResponse.StatusCode);
        var expected = await initialResponse.Content.ReadAsByteArrayAsync();

        await AssertRangeAsync(client, videoId, expected, start: 0, end: 15);
        await AssertRangeAsync(client, videoId, expected, start: 4096, end: 4159);

        Assert.Collection(
            fakeStore.CopyRequests,
            request =>
            {
                Assert.Equal(0, request.Offset);
                Assert.Equal(16, request.Length);
            },
            request =>
            {
                Assert.Equal(4096, request.Offset);
                Assert.Equal(64, request.Length);
            });
    }

    [Fact]
    public async Task First_byte_metric_records_sanitized_cache_and_range_categories()
    {
        const string videoId = "MetricTst1A";
        var observations = new ConcurrentQueue<MetricObservation>();
        using var listener = CreateFirstByteListener(observations);
        var fakeStore = new FakeObjectStore();
        _factory = CreateFactory(
            fakeStore,
            new SlowDeterministicExtractor(TimeSpan.FromMilliseconds(10)));

        using var client = _factory.CreateClient();
        await WaitForReadyAsync(client);

        using (var miss = await client.GetAsync($"/v1/tracks/{videoId}/audio"))
        {
            Assert.Equal(HttpStatusCode.OK, miss.StatusCode);
            await miss.Content.CopyToAsync(Stream.Null);
        }
        using (var initialHit = await client.GetAsync($"/v1/tracks/{videoId}/audio"))
        {
            Assert.Equal(HttpStatusCode.OK, initialHit.StatusCode);
            await initialHit.Content.CopyToAsync(Stream.Null);
        }
        using (var deepRange = new HttpRequestMessage(
                   HttpMethod.Get,
                   $"/v1/tracks/{videoId}/audio"))
        {
            deepRange.Headers.Range = new RangeHeaderValue(4096, 4127);
            using var nonzeroHit = await client.SendAsync(deepRange);
            Assert.Equal(HttpStatusCode.PartialContent, nonzeroHit.StatusCode);
            await nonzeroHit.Content.CopyToAsync(Stream.Null);
        }

        Assert.Contains(observations, sample =>
            sample is { Cache: "miss", Range: "initial", TagCount: 2 });
        Assert.Contains(observations, sample =>
            sample is { Cache: "hit", Range: "initial", TagCount: 2 });
        Assert.Contains(observations, sample =>
            sample is { Cache: "hit", Range: "nonzero", TagCount: 2 });
    }

    private WebApplicationFactory<Program> CreateFactory(IObjectStore store, IMediaExtractor extractor)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:PostgreSql", _postgres.GetConnectionString());
            builder.UseSetting("ObjectStorage:Endpoint", "http://minio-test:9000");
            builder.UseSetting("ObjectStorage:AccessKey", "test");
            builder.UseSetting("ObjectStorage:SecretKey", "test-key");
            builder.UseSetting("ObjectStorage:Bucket", "test-bucket");
            builder.UseSetting("Resolver:UseFakeExtractor", "true");
            builder.UseSetting("Resolver:LeaseDuration", "00:05:00");
            builder.UseSetting("Resolver:ExtractionTimeout", "00:00:30");
            builder.UseSetting("ENABLE_FAULT_INJECTION", "true");
            builder.UseSetting("Testing:AllowIncompleteProductionConfiguration", "true");

            builder.ConfigureServices(services =>
            {
                // Replace IObjectStore with our fake
                services.Remove(services.Single(s => s.ServiceType == typeof(IObjectStore)));
                services.AddSingleton(store);

                // Remove hosted services that depend on MinIO connectivity
                var hosted = services.Where(s =>
                    s.ServiceType == typeof(IHostedService) &&
                    (s.ImplementationType?.FullName?.Contains("ObjectStoreInitializer") == true ||
                     s.ImplementationType?.FullName?.Contains("ExpiredObjectJanitor") == true))
                    .ToList();
                foreach (var h in hosted) services.Remove(h);

                // Replace extractor
                services.Remove(services.Single(s => s.ServiceType == typeof(IMediaExtractor)));
                services.AddSingleton(extractor);
            });
        });
    }

    private static async Task WaitForReadyAsync(HttpClient client)
    {
        for (var i = 0; i < 30; i++)
        {
            try
            {
                var live = await client.GetAsync("/health/live");
                if (live.IsSuccessStatusCode) return;
            }
            catch { }
            await Task.Delay(500);
        }
    }

    private static async Task<StoredTrack?> PollTrackAsync(ITrackRepository repo, string videoId, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(500);
            var track = await repo.GetAsync(videoId, CancellationToken.None);
            if (track?.Status is TrackStatus.Ready or TrackStatus.Failed)
                return track;
        }
        return await repo.GetAsync(videoId, CancellationToken.None);
    }

    private static async Task AssertRangeAsync(
        HttpClient client,
        string videoId,
        byte[] expected,
        int start,
        int end)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/v1/tracks/{videoId}/audio");
        request.Headers.Range = new RangeHeaderValue(start, end);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(end - start + 1, response.Content.Headers.ContentLength);
        Assert.Equal(
            new ContentRangeHeaderValue(start, end, expected.LongLength),
            response.Content.Headers.ContentRange);
        Assert.Equal(
            expected.AsSpan(start, end - start + 1).ToArray(),
            await response.Content.ReadAsByteArrayAsync());
    }

    private static MeterListener CreateFirstByteListener(
        ConcurrentQueue<MetricObservation> observations)
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
        listener.SetMeasurementEventCallback<double>((_, duration, tags, _) =>
        {
            string? cache = null;
            string? range = null;
            var tagCount = 0;
            foreach (var tag in tags)
            {
                tagCount++;
                if (tag.Key == "cache") cache = tag.Value as string;
                if (tag.Key == "range") range = tag.Value as string;
            }
            observations.Enqueue(new MetricObservation(duration, cache, range, tagCount));
        });
        listener.Start();
        return listener;
    }

    private sealed record MetricObservation(
        double DurationSeconds,
        string? Cache,
        string? Range,
        int TagCount);
}

public sealed class SlowDeterministicExtractor(TimeSpan delay) : IMediaExtractor
{
    public async Task<byte[]> ExtractAsync(string videoId, CancellationToken cancellationToken)
    {
        await Task.Delay(delay, cancellationToken);
        return System.Text.Encoding.UTF8.GetBytes(
            "OggS\0HarmonyResolverFixture:" + videoId + new string('.', 8192));
    }
}

public sealed class FakeObjectStore : IObjectStore
{
    public byte[]? CapturedBytes { get; private set; }
    public List<(long Offset, long Length)> CopyRequests { get; } = [];

    public Task EnsureReadyAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task PutAsync(string objectKey, Stream source, long length,
        CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();
        await source.CopyToAsync(ms, cancellationToken);
        CapturedBytes = ms.ToArray();
    }

    public async Task CopyToAsync(string objectKey, Stream destination, long offset,
        long length, CancellationToken cancellationToken)
    {
        var bytes = CapturedBytes
            ?? throw new InvalidOperationException("Copy should not be called before upload completes.");
        CopyRequests.Add((offset, length));
        await destination.WriteAsync(
            bytes.AsMemory(checked((int)offset), checked((int)length)),
            cancellationToken);
    }

    public Task DeleteAsync(string objectKey, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
