using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Harmony.Resolver.Api.Infrastructure.Persistence;

#nullable disable

namespace Harmony.Resolver.Api.Infrastructure.Persistence.Migrations;

[DbContext(typeof(ResolverDbContext))]
[Migration("20260720011500_PermanentMediaAndPriority")]
public partial class PermanentMediaAndPriority : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("UPDATE resolver_tracks SET expires_at = NULL WHERE status = 'ready'");
        migrationBuilder.DropIndex(name: "ix_resolver_tracks_expiry", table: "resolver_tracks");
        migrationBuilder.Sql("DROP INDEX IF EXISTS ix_resolver_tracks_pending");
        migrationBuilder.AddColumn<int>(
            name: "priority",
            table: "resolver_tracks",
            type: "integer",
            nullable: false,
            defaultValue: 2);
        migrationBuilder.CreateIndex(
            name: "ix_resolver_tracks_pending",
            table: "resolver_tracks",
            columns: new[] { "priority", "created_at" },
            descending: new[] { true, false },
            filter: "status = 'ingesting'");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "ix_resolver_tracks_pending", table: "resolver_tracks");
        migrationBuilder.DropColumn(name: "priority", table: "resolver_tracks");
        migrationBuilder.CreateIndex(
            name: "ix_resolver_tracks_expiry",
            table: "resolver_tracks",
            column: "expires_at",
            filter: "status = 'ready'");
        migrationBuilder.CreateIndex(
            name: "ix_resolver_tracks_pending",
            table: "resolver_tracks",
            column: "created_at",
            filter: "status = 'ingesting'");
    }
}
