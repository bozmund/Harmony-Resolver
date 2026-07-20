using Harmony.Resolver.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harmony.Resolver.Api.Infrastructure.Persistence.Migrations;

[DbContext(typeof(ResolverDbContext))]
[Migration("20260720013000_BackupCandidates")]
public partial class BackupCandidates : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ingestion_kind",
            table: "resolver_tracks",
            type: "character varying(24)",
            maxLength: 24,
            nullable: false,
            defaultValue: "download");
        migrationBuilder.CreateTable(
            name: "resolver_backup_candidates",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                video_id = table.Column<string>(type: "character varying(11)", maxLength: 11, nullable: false),
                token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                staging_object_key = table.Column<string>(type: "text", nullable: true),
                content_length = table.Column<long>(type: "bigint", nullable: true),
                etag = table.Column<string>(type: "text", nullable: true),
                duration_seconds = table.Column<double>(type: "double precision", nullable: true),
                fingerprint_a = table.Column<string>(type: "text", nullable: true),
                fingerprint_b = table.Column<string>(type: "text", nullable: true),
                expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_resolver_backup_candidates", x => x.id);
                table.CheckConstraint("ck_resolver_backup_candidate_status",
                    "status IN ('pending', 'uploading', 'verifying', 'ready', 'rejected')");
            });
        migrationBuilder.CreateIndex(
            name: "ix_resolver_backup_candidates_expires_at",
            table: "resolver_backup_candidates",
            column: "expires_at");
        migrationBuilder.CreateIndex(
            name: "ix_resolver_backup_candidates_video_id",
            table: "resolver_backup_candidates",
            column: "video_id",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "resolver_backup_candidates");
        migrationBuilder.DropColumn(name: "ingestion_kind", table: "resolver_tracks");
    }
}
