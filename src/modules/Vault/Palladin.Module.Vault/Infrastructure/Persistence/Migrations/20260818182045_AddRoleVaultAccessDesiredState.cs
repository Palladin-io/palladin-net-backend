using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Palladin.Module.Vault.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRoleVaultAccessDesiredState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OrganizationMemberRoleSets",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleIds = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    Revision = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    AuthorizationVersion = table.Column<long>(type: "bigint", precision: 10, scale: 0, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizationMemberRoleSets", x => new { x.OrganizationId, x.UserId });
                });

            migrationBuilder.CreateTable(
                name: "OrganizationRoleDirectory",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    SourceRevision = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    MutationVersion = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizationRoleDirectory", x => new { x.OrganizationId, x.RoleId });
                });

            migrationBuilder.CreateTable(
                name: "RoleVaultAccessOperations",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    AddsAwaitingProvisioning = table.Column<int>(type: "integer", nullable: false),
                    RemovalsAwaitingSourceReconciliation = table.Column<int>(type: "integer", nullable: false),
                    Unchanged = table.Column<int>(type: "integer", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoleVaultAccessOperations", x => new { x.OrganizationId, x.Id });
                    table.UniqueConstraint("AK_RoleVaultAccessOperations_Id", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RoleVaultAccessPolicyDispatches",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    ChangedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    SelectedVaultIds = table.Column<Guid[]>(type: "uuid[]", nullable: false),
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
                    table.PrimaryKey("PK_RoleVaultAccessPolicyDispatches", x => new { x.OrganizationId, x.RoleId });
                });

            migrationBuilder.CreateTable(
                name: "RoleVaultAccessPolicySets",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    LastOperationId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoleVaultAccessPolicySets", x => new { x.OrganizationId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_RoleVaultAccessPolicySets_OrganizationRoleDirectory_Organiz~",
                        columns: x => new { x.OrganizationId, x.RoleId },
                        principalTable: "OrganizationRoleDirectory",
                        principalColumns: new[] { "OrganizationId", "RoleId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RoleVaultAccessPolicies",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    VaultId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoleVaultAccessPolicies", x => new { x.OrganizationId, x.RoleId, x.VaultId });
                    table.ForeignKey(
                        name: "FK_RoleVaultAccessPolicies_RoleVaultAccessPolicySets_Organizat~",
                        columns: x => new { x.OrganizationId, x.RoleId },
                        principalTable: "RoleVaultAccessPolicySets",
                        principalColumns: new[] { "OrganizationId", "RoleId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RoleVaultAccessPolicies_Vaults_OrganizationId_VaultId",
                        columns: x => new { x.OrganizationId, x.VaultId },
                        principalTable: "Vaults",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationMemberRoleSets_OrganizationId_IsActive",
                table: "OrganizationMemberRoleSets",
                columns: new[] { "OrganizationId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_RoleVaultAccessOperations_OrganizationId_RoleId_CreatedAt",
                table: "RoleVaultAccessOperations",
                columns: new[] { "OrganizationId", "RoleId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RoleVaultAccessPolicies_OrganizationId_VaultId",
                table: "RoleVaultAccessPolicies",
                columns: new[] { "OrganizationId", "VaultId" });

            migrationBuilder.CreateIndex(
                name: "IX_RoleVaultAccessPolicyDispatches_DispatchKey",
                table: "RoleVaultAccessPolicyDispatches",
                column: "DispatchKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RoleVaultAccessPolicyDispatches_NextAttemptAt_OrganizationI~",
                table: "RoleVaultAccessPolicyDispatches",
                columns: new[] { "NextAttemptAt", "OrganizationId", "RoleId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrganizationMemberRoleSets");

            migrationBuilder.DropTable(
                name: "RoleVaultAccessOperations");

            migrationBuilder.DropTable(
                name: "RoleVaultAccessPolicies");

            migrationBuilder.DropTable(
                name: "RoleVaultAccessPolicyDispatches");

            migrationBuilder.DropTable(
                name: "RoleVaultAccessPolicySets");

            migrationBuilder.DropTable(
                name: "OrganizationRoleDirectory");
        }
    }
}
