using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Palladin.Module.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInvitationRoleHistoryAndRefreshAuthorizationFence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "AuthorizationVersion",
                table: "RefreshTokens",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<string>(
                name: "RoleName",
                table: "OrganizationInvitations",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE "OrganizationInvitations" AS invitation
                SET "RoleName" = role."Name"
                FROM "Roles" AS role
                WHERE invitation."OrganizationId" = role."OrganizationId"
                  AND invitation."RoleId" = role."Id";
                """);

            migrationBuilder.AlterColumn<string>(
                name: "RoleName",
                table: "OrganizationInvitations",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100,
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "RoleId",
                table: "OrganizationInvitations",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AuthorizationVersion",
                table: "RefreshTokens");

            migrationBuilder.DropColumn(
                name: "RoleName",
                table: "OrganizationInvitations");

            migrationBuilder.Sql(
                """
                DELETE FROM "OrganizationInvitations"
                WHERE "RoleId" IS NULL;
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "RoleId",
                table: "OrganizationInvitations",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
