using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harmony.Resolver.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SnapshotRefresh : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "resolver_track_metadata");

            migrationBuilder.DropCheckConstraint(
                name: "ck_resolver_tracks_status",
                table: "resolver_tracks");

            migrationBuilder.DropColumn(
                name: "expires_at",
                table: "resolver_tracks");

            migrationBuilder.AddColumn<string>(
                name: "metadata",
                table: "resolver_tracks",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_resolver_tracks_status",
                table: "resolver_tracks",
                sql: "status IN ('metadata', 'ingesting', 'ready', 'failed')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_resolver_tracks_status",
                table: "resolver_tracks");

            migrationBuilder.DropColumn(
                name: "metadata",
                table: "resolver_tracks");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "expires_at",
                table: "resolver_tracks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "resolver_track_metadata",
                columns: table => new
                {
                    video_id = table.Column<string>(type: "character varying(11)", maxLength: 11, nullable: false),
                    album = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    artists = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    duration_seconds = table.Column<int>(type: "integer", nullable: true),
                    thumbnail_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_resolver_track_metadata", x => x.video_id);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_resolver_tracks_status",
                table: "resolver_tracks",
                sql: "status IN ('ingesting', 'ready', 'failed')");
        }
    }
}
