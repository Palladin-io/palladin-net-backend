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
                columns: new[] { "Kind", "Value" },
                unique: true);
        }
    }
}
