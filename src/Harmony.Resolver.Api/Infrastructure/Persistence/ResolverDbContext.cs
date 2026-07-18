using Harmony.Resolver.Api.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Harmony.Resolver.Api.Infrastructure.Persistence;

public sealed class ResolverDbContext(DbContextOptions<ResolverDbContext> options) : DbContext(options)
{
    public DbSet<TrackEntity> Tracks => Set<TrackEntity>();
    public DbSet<IngestionLeaseEntity> IngestionLeases => Set<IngestionLeaseEntity>();
    public DbSet<DiagnosticAuditEntity> DiagnosticAudits => Set<DiagnosticAuditEntity>();
    public DbSet<PlayEventEntity> PlayEvents => Set<PlayEventEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var tracks = modelBuilder.Entity<TrackEntity>();
        tracks.ToTable("resolver_tracks", table =>
        {
            table.HasCheckConstraint("ck_resolver_tracks_status", "status IN ('ingesting', 'ready', 'failed')");
            table.HasCheckConstraint("ck_resolver_tracks_ready_object", "(status = 'ready') = (object_key IS NOT NULL)");
        });
        tracks.HasKey(x => x.VideoId);
        tracks.Property(x => x.VideoId).HasColumnName("video_id").HasMaxLength(11);
        tracks.Property(x => x.Status).HasColumnName("status").HasMaxLength(16);
        tracks.Property(x => x.ObjectKey).HasColumnName("object_key");
        tracks.Property(x => x.ContentLength).HasColumnName("content_length");
        tracks.Property(x => x.ETag).HasColumnName("etag");
        tracks.Property(x => x.FailureCode).HasColumnName("failure_code").HasMaxLength(64);
        tracks.Property(x => x.RetryAfter).HasColumnName("retry_after");
        tracks.Property(x => x.LastAccessedAt).HasColumnName("last_accessed_at");
        tracks.Property(x => x.ExpiresAt).HasColumnName("expires_at");
        tracks.Property(x => x.CreatedAt).HasColumnName("created_at");
        tracks.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        tracks.HasIndex(x => x.ExpiresAt).HasDatabaseName("ix_resolver_tracks_expiry").HasFilter("status = 'ready'");
        tracks.HasIndex(x => x.UpdatedAt).HasDatabaseName("ix_resolver_tracks_failures").HasFilter("status = 'failed'").IsDescending();

        var leases = modelBuilder.Entity<IngestionLeaseEntity>();
        leases.ToTable("resolver_ingestion_leases", table => table.HasCheckConstraint("ck_resolver_leases_expiry", "expires_at > acquired_at"));
        leases.HasKey(x => x.VideoId);
        leases.Property(x => x.VideoId).HasColumnName("video_id").HasMaxLength(11);
        leases.Property(x => x.OwnerId).HasColumnName("owner_id");
        leases.Property(x => x.AcquiredAt).HasColumnName("acquired_at");
        leases.Property(x => x.ExpiresAt).HasColumnName("expires_at");
        leases.HasIndex(x => x.ExpiresAt).HasDatabaseName("ix_resolver_leases_expiry");
        leases.HasOne(x => x.Track).WithOne(x => x.Lease).HasForeignKey<IngestionLeaseEntity>(x => x.VideoId).OnDelete(DeleteBehavior.Cascade);

        var audits = modelBuilder.Entity<DiagnosticAuditEntity>();
        audits.ToTable("resolver_diagnostic_audit");
        audits.HasKey(x => x.Id);
        audits.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        audits.Property(x => x.SubjectHash).HasColumnName("subject_hash").HasMaxLength(128);
        audits.Property(x => x.ToolName).HasColumnName("tool_name").HasMaxLength(64);
        audits.Property(x => x.QuerySummary).HasColumnName("query_summary").HasColumnType("jsonb");
        audits.Property(x => x.OccurredAt).HasColumnName("occurred_at");

        var plays = modelBuilder.Entity<PlayEventEntity>();
        plays.ToTable("resolver_play_events");
        plays.HasKey(x => x.Id);
        plays.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        plays.Property(x => x.VideoId).HasColumnName("video_id").HasMaxLength(11);
        plays.Property(x => x.IdentityHash).HasColumnName("identity_hash").HasMaxLength(128);
        plays.Property(x => x.Cache).HasColumnName("cache").HasMaxLength(16);
        plays.Property(x => x.DurationMs).HasColumnName("duration_ms");
        plays.Property(x => x.PlayedAt).HasColumnName("played_at");
        plays.HasIndex(x => x.PlayedAt).HasDatabaseName("ix_resolver_play_events_played_at").IsDescending();
    }
}
