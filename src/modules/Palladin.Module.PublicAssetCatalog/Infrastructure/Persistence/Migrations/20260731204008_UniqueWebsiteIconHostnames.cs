using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Palladin.Module.PublicAssetCatalog.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UniqueWebsiteIconHostnames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PublicAssetAlias_Kind_Value",
                table: "PublicAssetAlias");

            // Pre-production data may contain duplicate hostname reservations
            // created by concurrent retries. Keep one deterministic asset and
            // discard the conflicting test assets before enforcing uniqueness.
            migrationBuilder.Sql("""
                WITH duplicate_assets AS (
                    SELECT "AssetId"
                    FROM (
                        SELECT "AssetId",
                               ROW_NUMBER() OVER (PARTITION BY "Kind", "Value" ORDER BY "AssetId") AS row_number
                        FROM "PublicAssetAlias"
                        WHERE "Kind" = 1
                    ) ranked
                    WHERE row_number > 1
                )
                DELETE FROM "Assets"
                WHERE "Id" IN (SELECT "AssetId" FROM duplicate_assets);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_PublicAssetAlias_Kind_Value",
                table: "PublicAssetAlias",
                columns: new[] { "Kind", "Value" },
                unique: true,
                filter: "\"Kind\" = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PublicAssetAlias_Kind_Value",
                table: "PublicAssetAlias");

            migrationBuilder.CreateIndex(
                name: "IX_PublicAssetAlias_Kind_Value",
                table: "PublicAssetAlias",
                columns: new[] { "Kind", "Value" });
        }
    }
}
