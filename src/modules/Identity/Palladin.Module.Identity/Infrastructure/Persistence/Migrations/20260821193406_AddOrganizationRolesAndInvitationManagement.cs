using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Palladin.Module.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrganizationRolesAndInvitationManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_OrganizationInvitations_Roles_OrganizationId_RoleId",
                table: "OrganizationInvitations");

            migrationBuilder.DropForeignKey(
                name: "FK_OrganizationMemberRoles_Roles_OrganizationId_RoleId",
                table: "OrganizationMemberRoles");

            migrationBuilder.DropIndex(
                name: "IX_Roles_OrganizationId_Name",
                table: "Roles");

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "Roles",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<string>(
                name: "NormalizedName",
                table: "Roles",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "AuthorizationVersion",
                table: "RefreshTokens",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<long>(
                name: "AuthorizationVersion",
                table: "OrganizationMembers",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<Instant>(
                name: "CancelledAt",
                table: "OrganizationInvitations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Instant>(
                name: "LastSentAt",
                table: "OrganizationInvitations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RoleName",
                table: "OrganizationInvitations",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE "Roles"
                SET "NormalizedName" = upper(btrim("Name"));

                UPDATE "OrganizationInvitations" AS invitation
                SET "RoleName" = role."Name",
                    "LastSentAt" = invitation."CreatedAt"
                FROM "Roles" AS role
                WHERE invitation."OrganizationId" = role."OrganizationId"
                  AND invitation."RoleId" = role."Id";
                """);

            migrationBuilder.AlterColumn<string>(
                name: "NormalizedName",
                table: "Roles",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100,
                oldNullable: true);

            migrationBuilder.AlterColumn<Instant>(
                name: "LastSentAt",
                table: "OrganizationInvitations",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(Instant),
                oldType: "timestamp with time zone",
                oldNullable: true);

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

            migrationBuilder.CreateIndex(
                name: "IX_Roles_OrganizationId_NormalizedName",
                table: "Roles",
                columns: new[] { "OrganizationId", "NormalizedName" },
                unique: true);

            migrationBuilder.Sql(
                """
                INSERT INTO "Roles" (
                    "OrganizationId",
                    "Id",
                    "Name",
                    "NormalizedName",
                    "Permissions",
                    "IsSystem",
                    "CreatedAt")
                SELECT
                    organization."Id",
                    md5(organization."Id"::text || ':palladin:system-role:user')::uuid,
                    'User',
                    'USER',
                    12,
                    TRUE,
                    CURRENT_TIMESTAMP
                FROM "Organizations" AS organization;
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_OrganizationInvitations_Roles_OrganizationId_RoleId",
                table: "OrganizationInvitations",
                columns: new[] { "OrganizationId", "RoleId" },
                principalTable: "Roles",
                principalColumns: new[] { "OrganizationId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_OrganizationMemberRoles_Roles_OrganizationId_RoleId",
                table: "OrganizationMemberRoles",
                columns: new[] { "OrganizationId", "RoleId" },
                principalTable: "Roles",
                principalColumns: new[] { "OrganizationId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DELETE FROM "Roles" AS role
                USING "Organizations" AS organization
                WHERE role."OrganizationId" = organization."Id"
                  AND role."Id" = md5(organization."Id"::text || ':palladin:system-role:user')::uuid
                  AND role."Name" = 'User'
                  AND role."NormalizedName" = 'USER'
                  AND role."Permissions" = 12
                  AND role."IsSystem" = TRUE;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_OrganizationInvitations_Roles_OrganizationId_RoleId",
                table: "OrganizationInvitations");

            migrationBuilder.DropForeignKey(
                name: "FK_OrganizationMemberRoles_Roles_OrganizationId_RoleId",
                table: "OrganizationMemberRoles");

            migrationBuilder.DropIndex(
                name: "IX_Roles_OrganizationId_NormalizedName",
                table: "Roles");

            migrationBuilder.DropColumn(
                name: "NormalizedName",
                table: "Roles");

            migrationBuilder.DropColumn(
                name: "AuthorizationVersion",
                table: "RefreshTokens");

            migrationBuilder.DropColumn(
                name: "AuthorizationVersion",
                table: "OrganizationMembers");

            migrationBuilder.DropColumn(
                name: "CancelledAt",
                table: "OrganizationInvitations");

            migrationBuilder.DropColumn(
                name: "LastSentAt",
                table: "OrganizationInvitations");

            migrationBuilder.DropColumn(
                name: "RoleName",
                table: "OrganizationInvitations");

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "Roles",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100);

            migrationBuilder.CreateIndex(
                name: "IX_Roles_OrganizationId_Name",
                table: "Roles",
                columns: new[] { "OrganizationId", "Name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_OrganizationInvitations_Roles_OrganizationId_RoleId",
                table: "OrganizationInvitations",
                columns: new[] { "OrganizationId", "RoleId" },
                principalTable: "Roles",
                principalColumns: new[] { "OrganizationId", "Id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_OrganizationMemberRoles_Roles_OrganizationId_RoleId",
                table: "OrganizationMemberRoles",
                columns: new[] { "OrganizationId", "RoleId" },
                principalTable: "Roles",
                principalColumns: new[] { "OrganizationId", "Id" },
                onDelete: ReferentialAction.Cascade);
        }
    }
}
