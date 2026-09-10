using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Palladin.Module.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IndexSharedUnlockOperationExpiry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_SharedUnlockOperations_ExpiresAt_Id",
                table: "SharedUnlockOperations",
                columns: new[] { "ExpiresAt", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SharedUnlockOperations_ExpiresAt_Id",
                table: "SharedUnlockOperations");
        }
    }
}
