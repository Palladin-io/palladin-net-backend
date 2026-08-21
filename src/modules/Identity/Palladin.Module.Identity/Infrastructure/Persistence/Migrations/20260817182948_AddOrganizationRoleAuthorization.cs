using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Palladin.Module.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrganizationRoleAuthorization : Migration
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

            migrationBuilder.Sql("""
                UPDATE "Roles"
                SET "NormalizedName" = upper(btrim("Name"));
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

            migrationBuilder.AddColumn<long>(
                name: "AuthorizationVersion",
                table: "OrganizationMembers",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.CreateIndex(
                name: "IX_Roles_OrganizationId_NormalizedName",
                table: "Roles",
                columns: new[] { "OrganizationId", "NormalizedName" },
                unique: true);

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
                table: "OrganizationMembers");

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
