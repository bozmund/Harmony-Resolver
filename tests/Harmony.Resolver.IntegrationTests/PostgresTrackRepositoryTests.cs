using Harmony.Resolver.Api.Domain;
using Harmony.Resolver.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace Harmony.Resolver.IntegrationTests;

public sealed class PostgresTrackRepositoryTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("harmony_tests")
        .WithUsername("harmony")
        .WithPassword("test-only-password")
        .Build();
    private readonly MutableTimeProvider _clock = new(DateTimeOffset.Parse("2026-07-10T20:00:00Z"));
    private IDbContextFactory<ResolverDbContext> _contexts = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        var options = new DbContextOptionsBuilder<ResolverDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options;
        _contexts = new TestDbContextFactory(options);
        await using var db = await _contexts.CreateDbContextAsync();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    [Fact]
    public async Task Twenty_concurrent_callers_produce_exactly_one_lease_winner()
    {
        var repository = new PostgresTrackRepository(_contexts, _clock);
        var attempts = Enumerable.Range(0, 20)
            .Select(_ => repository.TryAcquireLeaseAsync("dQw4w9WgXcQ", Guid.NewGuid(), TimeSpan.FromMinutes(2), CancellationToken.None));

        var leases = await Task.WhenAll(attempts);

        Assert.Single(leases, x => x is not null);
        Assert.Equal(TrackStatus.Ingesting, (await repository.GetAsync("dQw4w9WgXcQ", CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task Expired_lease_is_recoverable_and_old_owner_cannot_complete()
    {
        var repository = new PostgresTrackRepository(_contexts, _clock);
        var oldLease = await repository.TryAcquireLeaseAsync("aqz-KE-bpKQ", Guid.NewGuid(), TimeSpan.FromMinutes(2), CancellationToken.None);
        Assert.NotNull(oldLease);

        _clock.Advance(TimeSpan.FromMinutes(3));
        var recoveredLease = await repository.TryAcquireLeaseAsync("aqz-KE-bpKQ", Guid.NewGuid(), TimeSpan.FromMinutes(2), CancellationToken.None);

        Assert.NotNull(recoveredLease);
        Assert.NotEqual(oldLease!.OwnerId, recoveredLease!.OwnerId);
        Assert.False(await repository.MarkFailedAsync(oldLease, "stale_owner", _clock.GetUtcNow(), CancellationToken.None));
        Assert.True(await repository.MarkReadyAsync(recoveredLease, "tracks/aqz-KE-bpKQ.ogg", 42, "\"etag\"", _clock.GetUtcNow() + TimeSpan.FromDays(1), CancellationToken.None));
        Assert.Equal(TrackStatus.Ready, (await repository.GetAsync("aqz-KE-bpKQ", CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task Abandoning_a_lease_removes_the_orphaned_ingesting_track()
    {
        var repository = new PostgresTrackRepository(_contexts, _clock);
        var lease = await repository.TryAcquireLeaseAsync("zAbandon123", Guid.NewGuid(), TimeSpan.FromMinutes(2), CancellationToken.None);
        Assert.NotNull(lease);
        Assert.Equal(TrackStatus.Ingesting, (await repository.GetAsync("zAbandon123", CancellationToken.None))!.Status);

        await repository.AbandonLeaseAsync(lease!, CancellationToken.None);

        Assert.Null(await repository.GetAsync("zAbandon123", CancellationToken.None));
    }

    private sealed class TestDbContextFactory(DbContextOptions<ResolverDbContext> options) : IDbContextFactory<ResolverDbContext>
    {
        public ResolverDbContext CreateDbContext() => new(options);
    }

    private sealed class MutableTimeProvider(DateTimeOffset current) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan duration) => current += duration;
    }
}
