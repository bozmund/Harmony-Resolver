using Harmony.Resolver.Api.Abstractions;
using Harmony.Resolver.Api.Domain;
using Harmony.Resolver.Api.Infrastructure.Extraction;
using Harmony.Resolver.Api.Infrastructure.Persistence;
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

    public Task EnsureReadyAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task PutAsync(string objectKey, Stream source, long length,
        CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();
        await source.CopyToAsync(ms, cancellationToken);
        CapturedBytes = ms.ToArray();
    }

    public Task CopyToAsync(string objectKey, Stream destination, long offset,
        long length, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Copy should not be called on a fresh track.");

    public Task DeleteAsync(string objectKey, CancellationToken cancellationToken)
        => Task.CompletedTask;
}