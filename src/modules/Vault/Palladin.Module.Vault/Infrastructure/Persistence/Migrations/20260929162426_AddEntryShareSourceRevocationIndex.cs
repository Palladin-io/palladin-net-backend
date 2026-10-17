using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Palladin.Module.Vault.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEntryShareSourceRevocationIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_EntryShares_OrganizationId_CreatedBy_Id",
                table: "EntryShares",
                columns: new[] { "OrganizationId", "CreatedBy", "Id" },
                filter: "\"RevokedAt\" IS NULL AND \"ExpiredAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EntryShares_OrganizationId_CreatedBy_Id",
                table: "EntryShares");
        }
    }
}
