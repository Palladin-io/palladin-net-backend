using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Palladin.Module.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRoleVaultAccessDelegationMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsSystem",
                table: "OrganizationRoleVaultAccessDispatches",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "Permissions",
                table: "OrganizationRoleVaultAccessDispatches",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql(
                """
                UPDATE "OrganizationRoleVaultAccessDispatches" AS dispatch
                SET
                    "IsSystem" = role."IsSystem",
                    "Permissions" = role."Permissions"
                FROM "Roles" AS role
                WHERE role."OrganizationId" = dispatch."OrganizationId"
                  AND role."Id" = dispatch."RoleId";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsSystem",
                table: "OrganizationRoleVaultAccessDispatches");

            migrationBuilder.DropColumn(
                name: "Permissions",
                table: "OrganizationRoleVaultAccessDispatches");
        }
    }
}
