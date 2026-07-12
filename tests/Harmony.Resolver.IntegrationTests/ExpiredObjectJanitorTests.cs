using Harmony.Resolver.Api.Abstractions;
using Harmony.Resolver.Api.Configuration;
using Harmony.Resolver.Api.Domain;
using Harmony.Resolver.Api.Infrastructure.Persistence;
using Harmony.Resolver.Api.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using Xunit;

namespace Harmony.Resolver.IntegrationTests;

/// <summary>
/// Tests the periodic expiry sweep that deletes expired objects and their database rows.
/// </summary>
public sealed class ExpiredObjectJanitorTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("harmony_test_expiry")
        .WithUsername("harmony")
        .WithPassword("test-only-password")
        .Build();

    private IDbContextFactory<ResolverDbContext> _contexts = null!;
    private readonly MutableTimeProvider _clock = new(new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.Zero));

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        var options = new DbContextOptionsBuilder<ResolverDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options;
        _contexts = new Factory(options);
        await using var db = await _contexts.CreateDbContextAsync();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    [Fact]
    public async Task Janitor_sweeps_expired_tracks_and_calls_delete()
    {
        var store = new FakeJanitorStore();
        var repository = new PostgresTrackRepository(_contexts, _clock);

        // Seed: insert one track via the lease + MarkReady path
        var lease = await repository.TryAcquireLeaseAsync(
            "expired001", Guid.NewGuid(), TimeSpan.FromMinutes(2), CancellationToken.None);
        Assert.NotNull(lease);
        var ready = await repository.MarkReadyAsync(lease, "tracks/expired001.ogg",
            42, "\"etag1\"", _clock.GetUtcNow() + TimeSpan.FromHours(1), CancellationToken.None);
        Assert.True(ready);

        // Seed another track: complete MarkReady, then push expiry into the past
        lease = await repository.TryAcquireLeaseAsync(
            "expired002", Guid.NewGuid(), TimeSpan.FromMinutes(2), CancellationToken.None);
        Assert.NotNull(lease);
        ready = await repository.MarkReadyAsync(lease, "tracks/expired002.ogg",
            42, "\"etag2\"", _clock.GetUtcNow() + TimeSpan.FromHours(1), CancellationToken.None);
        Assert.True(ready);
        // Manually push the expiry into the past via direct SQL
        var past = _clock.GetUtcNow() - TimeSpan.FromMinutes(5);
        await using (var db = await _contexts.CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE resolver_tracks
                SET expires_at = {past}
                WHERE video_id = 'expired002'
                """);
        }

        // Seed a third track that is still fresh
        lease = await repository.TryAcquireLeaseAsync(
            "expired003", Guid.NewGuid(), TimeSpan.FromMinutes(2), CancellationToken.None);
        Assert.NotNull(lease);
        ready = await repository.MarkReadyAsync(lease, "tracks/expired003.ogg",
            99, "\"etag3\"", _clock.GetUtcNow() + TimeSpan.FromHours(2), CancellationToken.None);
        Assert.True(ready);

        // Verify pre-sweep state
        var all = await ListAllAsync();
        Assert.Contains(all, t => t.VideoId == "expired001");
        Assert.Contains(all, t => t.VideoId == "expired002");
        Assert.Contains(all, t => t.VideoId == "expired003");

        // Run janitor sweep
        var janitor = new ExpiredObjectJanitor(repository, store, _clock,
            new NullLogger<ExpiredObjectJanitor>());
        // Trigger the sweep directly (the timer is not running in tests)
        var sweepMethod = typeof(ExpiredObjectJanitor).GetMethod("SweepAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(sweepMethod);
        var sweepTask = (Task)sweepMethod.Invoke(janitor, [CancellationToken.None])!;
        await sweepTask;

        // Verify expired track was cleaned up
        var remaining = await ListAllAsync();
        Assert.DoesNotContain(remaining, t => t.VideoId == "expired002");
        Assert.Contains(remaining, t => t.VideoId == "expired001");
        Assert.Contains(remaining, t => t.VideoId == "expired003");

        // Verify the store's DeleteAsync was called for the expired object
        Assert.Contains(store.DeletedKeys, k => k == "tracks/expired002.ogg");
        Assert.DoesNotContain(store.DeletedKeys, k => k == "tracks/expired001.ogg");
        Assert.DoesNotContain(store.DeletedKeys, k => k == "tracks/expired003.ogg");
    }

    private async Task<List<StoredTrack>> ListAllAsync()
    {
        var results = new List<StoredTrack>();
        // Read them one by one via GetAsync for the three IDs
        var ids = new[] { "expired001", "expired002", "expired003" };
        var repo = new PostgresTrackRepository(_contexts, _clock);
        foreach (var id in ids)
        {
            var track = await repo.GetAsync(id, CancellationToken.None);
            if (track is not null) results.Add(track);
        }
        return results;
    }

    private sealed class Factory(DbContextOptions<ResolverDbContext> options) : IDbContextFactory<ResolverDbContext>
    {
        public ResolverDbContext CreateDbContext() => new(options);
    }

    private sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _current = start;
        public override DateTimeOffset GetUtcNow() => _current;
        public void Advance(TimeSpan delta) => _current += delta;
    }

    private sealed class FakeJanitorStore : IObjectStore
    {
        public List<string> DeletedKeys { get; } = [];

        public Task EnsureReadyAsync(CancellationToken ct) => Task.CompletedTask;
        public Task PutAsync(string key, Stream source, long length, CancellationToken ct)
            => Task.CompletedTask;
        public Task CopyToAsync(string key, Stream dest, long offset, long length, CancellationToken ct)
            => Task.CompletedTask;
        public Task DeleteAsync(string objectKey, CancellationToken ct)
        {
            DeletedKeys.Add(objectKey);
            return Task.CompletedTask;
        }
    }
}