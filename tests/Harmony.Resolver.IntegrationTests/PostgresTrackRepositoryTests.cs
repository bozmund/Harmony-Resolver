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

    [Fact]
    public async Task Enqueue_creates_a_pending_ingesting_job_that_can_be_claimed()
    {
        var repository = new PostgresTrackRepository(_contexts, _clock);
        await repository.EnqueueAsync("pendingVid1", CancellationToken.None);
        Assert.Equal(TrackStatus.Ingesting, (await repository.GetAsync("pendingVid1", CancellationToken.None))!.Status);

        var lease = await repository.ClaimJobAsync(Guid.NewGuid(), TimeSpan.FromMinutes(3), CancellationToken.None);
        Assert.NotNull(lease);
        Assert.Equal("pendingVid1", lease!.VideoId);
    }

    [Fact]
    public async Task Enqueue_is_idempotent_and_leaves_a_ready_track_untouched()
    {
        var repository = new PostgresTrackRepository(_contexts, _clock);
        var lease = await repository.TryAcquireLeaseAsync("readyTrack1", Guid.NewGuid(), TimeSpan.FromMinutes(3), CancellationToken.None);
        await repository.MarkReadyAsync(lease!, "tracks/readyTrack1.ogg", 1234, "\"etag-abc\"", _clock.GetUtcNow() + TimeSpan.FromDays(1), CancellationToken.None);

        await repository.EnqueueAsync("readyTrack1", CancellationToken.None);

        var track = await repository.GetAsync("readyTrack1", CancellationToken.None);
        Assert.Equal(TrackStatus.Ready, track!.Status);
        Assert.Equal("tracks/readyTrack1.ogg", track.ObjectKey);
        Assert.Equal(1234, track.ContentLength);
    }

    [Fact]
    public async Task Enqueue_revives_a_failed_job_only_after_its_retry_backoff()
    {
        var repository = new PostgresTrackRepository(_contexts, _clock);
        var lease = await repository.TryAcquireLeaseAsync("failedVid01", Guid.NewGuid(), TimeSpan.FromMinutes(3), CancellationToken.None);
        await repository.MarkFailedAsync(lease!, "yt_dlp_failed", _clock.GetUtcNow() + TimeSpan.FromMinutes(5), CancellationToken.None);

        await repository.EnqueueAsync("failedVid01", CancellationToken.None);
        Assert.Equal(TrackStatus.Failed, (await repository.GetAsync("failedVid01", CancellationToken.None))!.Status);

        _clock.Advance(TimeSpan.FromMinutes(6));
        await repository.EnqueueAsync("failedVid01", CancellationToken.None);
        Assert.Equal(TrackStatus.Ingesting, (await repository.GetAsync("failedVid01", CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task ClaimJob_returns_null_when_no_jobs_are_pending()
    {
        var repository = new PostgresTrackRepository(_contexts, _clock);
        Assert.Null(await repository.ClaimJobAsync(Guid.NewGuid(), TimeSpan.FromMinutes(3), CancellationToken.None));
    }

    [Fact]
    public async Task ClaimJob_leases_the_oldest_pending_job()
    {
        var repository = new PostgresTrackRepository(_contexts, _clock);
        await repository.EnqueueAsync("oldestJob01", CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(5));
        await repository.EnqueueAsync("newerJob002", CancellationToken.None);

        var lease = await repository.ClaimJobAsync(Guid.NewGuid(), TimeSpan.FromMinutes(3), CancellationToken.None);
        Assert.NotNull(lease);
        Assert.Equal("oldestJob01", lease!.VideoId);
    }

    [Fact]
    public async Task One_pending_job_is_claimed_by_exactly_one_of_many_workers()
    {
        var repository = new PostgresTrackRepository(_contexts, _clock);
        await repository.EnqueueAsync("dQw4w9WgXcQ", CancellationToken.None);

        var attempts = Enumerable.Range(0, 20)
            .Select(_ => repository.ClaimJobAsync(Guid.NewGuid(), TimeSpan.FromMinutes(3), CancellationToken.None));
        var leases = await Task.WhenAll(attempts);

        Assert.Single(leases, x => x is not null);
    }

    [Fact]
    public async Task A_claimed_job_can_be_committed_by_the_claiming_worker()
    {
        var repository = new PostgresTrackRepository(_contexts, _clock);
        await repository.EnqueueAsync("commitVid01", CancellationToken.None);
        var lease = await repository.ClaimJobAsync(Guid.NewGuid(), TimeSpan.FromMinutes(3), CancellationToken.None);
        Assert.NotNull(lease);

        var committed = await repository.MarkReadyAsync(lease!, "tracks/commitVid01.ogg", 999, "\"etag\"",
            _clock.GetUtcNow() + TimeSpan.FromDays(1), CancellationToken.None);
        Assert.True(committed);
        Assert.Equal(TrackStatus.Ready, (await repository.GetAsync("commitVid01", CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task FailStuckJobs_fails_unleased_jobs_and_spares_actively_leased_ones()
    {
        var repository = new PostgresTrackRepository(_contexts, _clock);
        await repository.EnqueueAsync("stuckJob001", CancellationToken.None);
        await repository.EnqueueAsync("leasedJob01", CancellationToken.None);
        var claimed = await repository.ClaimJobAsync(Guid.NewGuid(), TimeSpan.FromMinutes(3), CancellationToken.None);
        Assert.NotNull(claimed);

        _clock.Advance(TimeSpan.FromMinutes(1)); // both jobs are now "old"; the claimed lease is still live
        var failed = await repository.FailStuckJobsAsync(_clock.GetUtcNow(),
            _clock.GetUtcNow() + TimeSpan.FromMinutes(15), CancellationToken.None);

        Assert.Equal(1, failed);
        var stuckId = claimed!.VideoId == "stuckJob001" ? "leasedJob01" : "stuckJob001";
        Assert.Equal(TrackStatus.Failed, (await repository.GetAsync(stuckId, CancellationToken.None))!.Status);
        Assert.Equal(TrackStatus.Ingesting, (await repository.GetAsync(claimed.VideoId, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task ListPendingJobs_returns_unleased_ingesting_jobs_oldest_first()
    {
        var repository = new PostgresTrackRepository(_contexts, _clock);
        await repository.EnqueueAsync("pendingaaaa", CancellationToken.None); // oldest
        _clock.Advance(TimeSpan.FromSeconds(5));
        await repository.EnqueueAsync("pendingbbbb", CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(5));
        await repository.EnqueueAsync("pendingcccc", CancellationToken.None);

        // Claiming leases the oldest (pendingaaaa); a leased job must be excluded from the pending list.
        var claimed = await repository.ClaimJobAsync(Guid.NewGuid(), TimeSpan.FromMinutes(3), CancellationToken.None);
        Assert.Equal("pendingaaaa", claimed!.VideoId);
        // A ready job must be excluded too.
        var readyLease = await repository.TryAcquireLeaseAsync("readyddd001", Guid.NewGuid(), TimeSpan.FromMinutes(3), CancellationToken.None);
        await repository.MarkReadyAsync(readyLease!, "tracks/readyddd001.ogg", 1, "\"e\"", _clock.GetUtcNow() + TimeSpan.FromDays(1), CancellationToken.None);

        var pending = await repository.ListPendingJobsAsync(_clock.GetUtcNow(), 50, CancellationToken.None);

        Assert.Equal(new[] { "pendingbbbb", "pendingcccc" }, pending.ToArray());
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
