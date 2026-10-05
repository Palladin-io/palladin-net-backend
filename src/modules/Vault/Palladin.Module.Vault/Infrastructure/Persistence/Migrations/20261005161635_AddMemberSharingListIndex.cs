using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Palladin.Module.Vault.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMemberSharingListIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_EntryShares_OrganizationId_CreatedBy_CreatedAt_Id",
                table: "EntryShares",
                columns: new[] { "OrganizationId", "CreatedBy", "CreatedAt", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EntryShares_OrganizationId_CreatedBy_CreatedAt_Id",
                table: "EntryShares");
        }
    }
}
