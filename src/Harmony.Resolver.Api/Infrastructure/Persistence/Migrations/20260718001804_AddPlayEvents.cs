using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Harmony.Resolver.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlayEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "resolver_play_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    video_id = table.Column<string>(type: "character varying(11)", maxLength: 11, nullable: false),
                    identity_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    cache = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    duration_ms = table.Column<long>(type: "bigint", nullable: false),
                    played_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_resolver_play_events", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_resolver_play_events_played_at",
                table: "resolver_play_events",
                column: "played_at",
                descending: new bool[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "resolver_play_events");
        }
    }
}
