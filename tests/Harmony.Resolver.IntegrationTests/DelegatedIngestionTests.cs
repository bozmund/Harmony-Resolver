using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harmony.Resolver.Api.Abstractions;
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
/// Drives the delegated-extraction worker protocol end-to-end over HTTP: a cache miss enqueues a job,
/// a worker claims it, heartbeats, and reports failure, and the listener then sees the failure. Uses
/// real PostgreSQL + a fake object store; the ffmpeg-dependent upload leg is covered structurally by
/// the existing MarkReady commit path (see <see cref="PostgresTrackRepositoryTests"/>).
/// </summary>
public sealed class DelegatedIngestionTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("harmony_test_delegated")
        .WithUsername("harmony")
        .WithPassword("test-only-password")
        .Build();

    private readonly CapturingJobNotifier _notifier = new();
    private WebApplicationFactory<Program> _factory = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var db = new ResolverDbContext(new DbContextOptionsBuilder<ResolverDbContext>()
            .UseNpgsql(_postgres.GetConnectionString()).Options);
        await db.Database.MigrateAsync();
        _factory = CreateFactory();
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Cache_miss_enqueues_a_job_a_worker_can_claim_heartbeat_and_fail()
    {
        const string videoId = "DelegateTst";
        using var client = _factory.CreateClient();
        await WaitForReadyAsync(client);

        // Queue is empty until a listener misses.
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/v1/worker/jobs/claim", null)).StatusCode);

        // A cache miss enqueues the job and tells the listener to poll.
        var miss = await client.GetAsync($"/v1/tracks/{videoId}/audio");
        Assert.Equal(HttpStatusCode.Accepted, miss.StatusCode);

        // The worker claims the pending job.
        var claim = await client.PostAsync("/v1/worker/jobs/claim", null);
        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        using var claimDoc = JsonDocument.Parse(await claim.Content.ReadAsStringAsync());
        Assert.Equal(videoId, claimDoc.RootElement.GetProperty("videoId").GetString());
        var leaseToken = claimDoc.RootElement.GetProperty("leaseToken").GetString()!;

        // With the job claimed, the queue is empty again.
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/v1/worker/jobs/claim", null)).StatusCode);

        // Heartbeat keeps the lease alive.
        using var heartbeat = new HttpRequestMessage(HttpMethod.Post, $"/v1/worker/jobs/{videoId}/heartbeat");
        heartbeat.Headers.Add("X-Lease-Token", leaseToken);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(heartbeat)).StatusCode);

        // The worker reports a failure; the job transitions to failed.
        using var fail = new HttpRequestMessage(HttpMethod.Post, $"/v1/worker/tracks/{videoId}/fail")
        {
            Content = JsonContent.Create(new { code = "yt_dlp_failed" })
        };
        fail.Headers.Add("X-Lease-Token", leaseToken);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(fail)).StatusCode);

        // The listener now observes the failure within its retry window.
        var afterFail = await client.GetAsync($"/v1/tracks/{videoId}/audio");
        Assert.Equal(HttpStatusCode.BadGateway, afterFail.StatusCode);
    }

    [Fact]
    public async Task Heartbeat_without_a_lease_token_is_rejected()
    {
        using var client = _factory.CreateClient();
        await WaitForReadyAsync(client);
        var response = await client.PostAsync("/v1/worker/jobs/aBcDeFgHiJk/heartbeat", null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Cache_miss_publishes_a_job_notification()
    {
        const string videoId = "NotifyTest1";
        using var client = _factory.CreateClient();
        await WaitForReadyAsync(client);

        var miss = await client.GetAsync($"/v1/tracks/{videoId}/audio");
        Assert.Equal(HttpStatusCode.Accepted, miss.StatusCode);

        Assert.Contains(videoId, _notifier.Notified);
    }

    private WebApplicationFactory<Program> CreateFactory()
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:PostgreSql", _postgres.GetConnectionString());
            builder.UseSetting("ObjectStorage:Endpoint", "http://minio-test:9000");
            builder.UseSetting("ObjectStorage:AccessKey", "test");
            builder.UseSetting("ObjectStorage:SecretKey", "test-key");
            builder.UseSetting("ObjectStorage:Bucket", "test-bucket");
            builder.UseSetting("Resolver:ExtractionMode", "Delegated");
            builder.UseSetting("Resolver:LeaseDuration", "00:05:00");
            builder.UseSetting("Testing:AllowIncompleteProductionConfiguration", "true");

            builder.ConfigureServices(services =>
            {
                services.Remove(services.Single(s => s.ServiceType == typeof(IObjectStore)));
                services.AddSingleton<IObjectStore>(new FakeObjectStore());
                services.Remove(services.Single(s => s.ServiceType == typeof(IJobNotifier)));
                services.AddSingleton<IJobNotifier>(_notifier);

                var hosted = services.Where(s => s.ServiceType == typeof(IHostedService) &&
                    (s.ImplementationType?.FullName?.Contains("ObjectStoreInitializer") == true ||
                     s.ImplementationType?.FullName?.Contains("ExpiredObjectJanitor") == true ||
                     s.ImplementationType?.FullName?.Contains("StuckJobReaper") == true))
                    .ToList();
                foreach (var h in hosted) services.Remove(h);
            });
        });
    }

    private static async Task WaitForReadyAsync(HttpClient client)
    {
        for (var i = 0; i < 30; i++)
        {
            try { if ((await client.GetAsync("/health/live")).IsSuccessStatusCode) return; }
            catch { /* not up yet */ }
            await Task.Delay(500);
        }
    }

    private sealed class CapturingJobNotifier : IJobNotifier
    {
        public System.Collections.Concurrent.ConcurrentBag<string> Notified { get; } = [];

        public Task NotifyAsync(string videoId, CancellationToken cancellationToken)
        {
            Notified.Add(videoId);
            return Task.CompletedTask;
        }
    }
}
