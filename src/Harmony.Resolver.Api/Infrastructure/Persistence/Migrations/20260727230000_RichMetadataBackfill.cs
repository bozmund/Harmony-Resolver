using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harmony.Resolver.Api.Infrastructure.Persistence.Migrations;

/// <summary>
/// Queues a metadata refetch for every track whose stored document has no album browse id.
///
/// Metadata used to come from yt-dlp or the watch page, both of which describe a *video*: channel
/// as artist, letterboxed thumbnail, an album name at best. The fleet now reads YouTube Music
/// instead, which is the only source of an album id, artist ids and square cover art — so the rows
/// written before that need re-fetching to catch up.
///
/// Schema-free on purpose: the jobs table already exists, and metadata lives in
/// <c>resolver_tracks.metadata</c>. Metadata jobs are only claimed when no audio job is available,
/// so this never competes with ingestion.
/// </summary>
[DbContext(typeof(ResolverDbContext))]
[Migration("20260727230000_RichMetadataBackfill")]
public partial class RichMetadataBackfill : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("""
            INSERT INTO resolver_metadata_backfill_jobs (video_id, status, created_at, updated_at)
            SELECT video_id, 'pending', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
            FROM resolver_tracks
            WHERE metadata IS NULL OR metadata->'album'->>'id' IS NULL
            ON CONFLICT (video_id) DO NOTHING;
            """);

    /// <summary>
    /// Queued work, not schema. Reverting the migration must not delete jobs that may already have
    /// been claimed or completed, and leaving them costs nothing.
    /// </summary>
    protected override void Down(MigrationBuilder migrationBuilder)
    {
    }
}
