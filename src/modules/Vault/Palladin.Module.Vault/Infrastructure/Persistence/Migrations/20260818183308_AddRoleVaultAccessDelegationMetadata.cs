using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Palladin.Module.Vault.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRoleVaultAccessDelegationMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsSystem",
                table: "OrganizationRoleDirectory",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "Permissions",
                table: "OrganizationRoleDirectory",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsSystem",
                table: "OrganizationRoleDirectory");

            migrationBuilder.DropColumn(
                name: "Permissions",
                table: "OrganizationRoleDirectory");
        }
    }
}
