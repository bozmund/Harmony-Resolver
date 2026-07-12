using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Harmony.Resolver.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialResolverSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "resolver_diagnostic_audit",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    subject_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    tool_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    query_summary = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_resolver_diagnostic_audit", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "resolver_tracks",
                columns: table => new
                {
                    video_id = table.Column<string>(type: "character varying(11)", maxLength: 11, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    object_key = table.Column<string>(type: "text", nullable: true),
                    content_length = table.Column<long>(type: "bigint", nullable: true),
                    etag = table.Column<string>(type: "text", nullable: true),
                    failure_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    retry_after = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_accessed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_resolver_tracks", x => x.video_id);
                    table.CheckConstraint("ck_resolver_tracks_ready_object", "(status = 'ready') = (object_key IS NOT NULL)");
                    table.CheckConstraint("ck_resolver_tracks_status", "status IN ('ingesting', 'ready', 'failed')");
                });

            migrationBuilder.CreateTable(
                name: "resolver_ingestion_leases",
                columns: table => new
                {
                    video_id = table.Column<string>(type: "character varying(11)", maxLength: 11, nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    acquired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_resolver_ingestion_leases", x => x.video_id);
                    table.CheckConstraint("ck_resolver_leases_expiry", "expires_at > acquired_at");
                    table.ForeignKey(
                        name: "FK_resolver_ingestion_leases_resolver_tracks_video_id",
                        column: x => x.video_id,
                        principalTable: "resolver_tracks",
                        principalColumn: "video_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_resolver_leases_expiry",
                table: "resolver_ingestion_leases",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_resolver_tracks_expiry",
                table: "resolver_tracks",
                column: "expires_at",
                filter: "status = 'ready'");

            migrationBuilder.CreateIndex(
                name: "ix_resolver_tracks_failures",
                table: "resolver_tracks",
                column: "updated_at",
                descending: new bool[0],
                filter: "status = 'failed'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "resolver_diagnostic_audit");

            migrationBuilder.DropTable(
                name: "resolver_ingestion_leases");

            migrationBuilder.DropTable(
                name: "resolver_tracks");
        }
    }
}
