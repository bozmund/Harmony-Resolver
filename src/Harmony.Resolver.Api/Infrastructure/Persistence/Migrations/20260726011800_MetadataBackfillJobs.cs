using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harmony.Resolver.Api.Infrastructure.Persistence.Migrations;

[DbContext(typeof(ResolverDbContext))]
[Migration("20260726011800_MetadataBackfillJobs")]
public partial class MetadataBackfillJobs : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "resolver_metadata_backfill_jobs",
            columns: table => new
            {
                video_id = table.Column<string>(type: "character varying(11)", maxLength: 11, nullable: false),
                status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                retry_after = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_resolver_metadata_backfill_jobs", x => x.video_id);
                table.CheckConstraint("ck_resolver_metadata_jobs_status", "status IN ('pending', 'failed')");
            });
        migrationBuilder.CreateTable(
            name: "resolver_metadata_backfill_leases",
            columns: table => new
            {
                video_id = table.Column<string>(type: "character varying(11)", maxLength: 11, nullable: false),
                owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                acquired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_resolver_metadata_backfill_leases", x => x.video_id);
                table.CheckConstraint("ck_resolver_metadata_leases_expiry", "expires_at > acquired_at");
                table.ForeignKey("fk_resolver_metadata_backfill_leases_resolver_metadata_backfill_jobs_video_id", x => x.video_id,
                    principalTable: "resolver_metadata_backfill_jobs", principalColumn: "video_id", onDelete: ReferentialAction.Cascade);
            });
        migrationBuilder.CreateIndex(name: "ix_resolver_metadata_jobs_pending", table: "resolver_metadata_backfill_jobs", columns: new[] { "status", "created_at" }, filter: "status = 'pending'");
        migrationBuilder.CreateIndex(name: "ix_resolver_metadata_leases_expiry", table: "resolver_metadata_backfill_leases", column: "expires_at");
        migrationBuilder.Sql("""
            INSERT INTO resolver_metadata_backfill_jobs (video_id, status, created_at, updated_at)
            SELECT t.video_id, 'pending', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
            FROM resolver_tracks t
            LEFT JOIN resolver_track_metadata m ON m.video_id = t.video_id
            WHERE t.status = 'ready' AND (m.video_id IS NULL OR m.title IS NULL)
            ON CONFLICT (video_id) DO NOTHING;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "resolver_metadata_backfill_leases");
        migrationBuilder.DropTable(name: "resolver_metadata_backfill_jobs");
    }
}
