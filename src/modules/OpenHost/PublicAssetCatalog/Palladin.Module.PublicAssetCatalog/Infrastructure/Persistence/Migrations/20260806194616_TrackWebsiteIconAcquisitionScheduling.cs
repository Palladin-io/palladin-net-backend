using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Palladin.Module.PublicAssetCatalog.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TrackWebsiteIconAcquisitionScheduling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Instant>(
                name: "AcquisitionScheduledAt",
                table: "Assets",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AcquisitionScheduledAt",
                table: "Assets");
        }
    }
}
