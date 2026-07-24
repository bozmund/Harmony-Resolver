using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harmony.Resolver.Api.Infrastructure.Persistence.Migrations;

[DbContext(typeof(ResolverDbContext))]
[Migration("20260725001500_TrackMetadata")]
public partial class TrackMetadata : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "resolver_track_metadata",
            columns: table => new
            {
                video_id = table.Column<string>(type: "character varying(11)", maxLength: 11, nullable: false),
                title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                artists = table.Column<string>(type: "jsonb", nullable: true),
                album = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                duration_seconds = table.Column<int>(type: "integer", nullable: true),
                thumbnail_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("pk_resolver_track_metadata", x => x.video_id));
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "resolver_track_metadata");
    }
}
