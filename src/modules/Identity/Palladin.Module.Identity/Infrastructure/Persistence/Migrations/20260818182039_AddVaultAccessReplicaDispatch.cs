using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Palladin.Module.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVaultAccessReplicaDispatch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "VaultAccessRevision",
                table: "Roles",
                type: "numeric(20,0)",
                precision: 20,
                scale: 0,
                nullable: false,
                defaultValue: 1m);

            migrationBuilder.AddColumn<decimal>(
                name: "VaultAccessRevision",
                table: "OrganizationMembers",
                type: "numeric(20,0)",
                precision: 20,
                scale: 0,
                nullable: false,
                defaultValue: 1m);

            migrationBuilder.CreateTable(
                name: "OrganizationMemberRoleSetDispatches",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleIds = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    Revision = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    AuthorizationVersion = table.Column<long>(type: "bigint", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    DispatchKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PublishedRevision = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    PublishAttempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    LastAttemptAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    LastErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PublishedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizationMemberRoleSetDispatches", x => new { x.OrganizationId, x.UserId });
                });

            migrationBuilder.CreateTable(
                name: "OrganizationRoleVaultAccessDispatches",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    Change = table.Column<int>(type: "integer", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OccurredAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    DispatchKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PublishedRevision = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    PublishAttempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    LastAttemptAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    LastErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PublishedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizationRoleVaultAccessDispatches", x => new { x.OrganizationId, x.RoleId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationMemberRoleSetDispatches_DispatchKey",
                table: "OrganizationMemberRoleSetDispatches",
                column: "DispatchKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationMemberRoleSetDispatches_NextAttemptAt_Organizat~",
                table: "OrganizationMemberRoleSetDispatches",
                columns: new[] { "NextAttemptAt", "OrganizationId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationRoleVaultAccessDispatches_DispatchKey",
                table: "OrganizationRoleVaultAccessDispatches",
                column: "DispatchKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationRoleVaultAccessDispatches_NextAttemptAt_Organiz~",
                table: "OrganizationRoleVaultAccessDispatches",
                columns: new[] { "NextAttemptAt", "OrganizationId", "RoleId" });

            migrationBuilder.Sql(
                """
                INSERT INTO "OrganizationRoleVaultAccessDispatches" (
                    "OrganizationId", "RoleId", "Revision", "Change", "IsDeleted", "OccurredAt",
                    "DispatchKey", "PublishedRevision", "PublishAttempts", "NextAttemptAt")
                SELECT
                    role."OrganizationId",
                    role."Id",
                    role."VaultAccessRevision",
                    2,
                    FALSE,
                    role."CreatedAt",
                    'organization-role:'
                        || replace(role."OrganizationId"::text, '-', '') || ':'
                        || replace(role."Id"::text, '-', '') || ':'
                        || role."VaultAccessRevision"::text,
                    0,
                    0,
                    role."CreatedAt"
                FROM "Roles" AS role;
                """);

            migrationBuilder.Sql(
                """
                INSERT INTO "OrganizationMemberRoleSetDispatches" (
                    "OrganizationId", "UserId", "RoleIds", "Revision", "AuthorizationVersion",
                    "IsActive", "UpdatedAt", "DispatchKey", "PublishedRevision", "PublishAttempts",
                    "NextAttemptAt")
                SELECT
                    member."OrganizationId",
                    member."UserId",
                    COALESCE(
                        array_agg(assignment."RoleId" ORDER BY assignment."RoleId")
                            FILTER (WHERE assignment."RoleId" IS NOT NULL),
                        ARRAY[]::uuid[]),
                    member."VaultAccessRevision",
                    member."AuthorizationVersion",
                    member."Status" = 1,
                    member."UpdatedAt",
                    'organization-member-role-set:'
                        || replace(member."OrganizationId"::text, '-', '') || ':'
                        || replace(member."UserId"::text, '-', '') || ':'
                        || member."VaultAccessRevision"::text,
                    0,
                    0,
                    member."UpdatedAt"
                FROM "OrganizationMembers" AS member
                LEFT JOIN "OrganizationMemberRoles" AS assignment
                    ON assignment."OrganizationId" = member."OrganizationId"
                   AND assignment."UserId" = member."UserId"
                GROUP BY member."OrganizationId", member."UserId";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrganizationMemberRoleSetDispatches");

            migrationBuilder.DropTable(
                name: "OrganizationRoleVaultAccessDispatches");

            migrationBuilder.DropColumn(
                name: "VaultAccessRevision",
                table: "Roles");

            migrationBuilder.DropColumn(
                name: "VaultAccessRevision",
                table: "OrganizationMembers");
        }
    }
}
