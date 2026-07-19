using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harmony.Resolver.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPendingJobIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_resolver_tracks_pending",
                table: "resolver_tracks",
                column: "created_at",
                filter: "status = 'ingesting'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_resolver_tracks_pending",
                table: "resolver_tracks");
        }
    }
}
