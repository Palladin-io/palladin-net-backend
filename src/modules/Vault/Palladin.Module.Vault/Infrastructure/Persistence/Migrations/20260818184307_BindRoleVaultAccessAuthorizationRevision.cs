using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Palladin.Module.Vault.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BindRoleVaultAccessAuthorizationRevision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AuthorizedByPermissions",
                table: "RoleVaultAccessPolicySets",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "AuthorizedRoleRevision",
                table: "RoleVaultAccessPolicySets",
                type: "numeric(20,0)",
                precision: 20,
                scale: 0,
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AuthorizedByPermissions",
                table: "RoleVaultAccessPolicySets");

            migrationBuilder.DropColumn(
                name: "AuthorizedRoleRevision",
                table: "RoleVaultAccessPolicySets");
        }
    }
}
