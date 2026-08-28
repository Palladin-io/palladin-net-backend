using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Palladin.Module.Vault.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddScriptExecutionPackages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "GrantType",
                table: "Grants",
                type: "character varying(21)",
                maxLength: 21,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(8)",
                oldMaxLength: 8);

            migrationBuilder.AddColumn<Guid>(
                name: "ScriptEntryId",
                table: "Grants",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ScriptExecutionPackages",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    VaultId = table.Column<Guid>(type: "uuid", nullable: false),
                    GrantId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentAccessEpoch = table.Column<long>(type: "bigint", nullable: false),
                    ScriptEntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScriptRevision = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    PackageRevision = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    ContractVersion = table.Column<int>(type: "integer", nullable: false),
                    RecipientAgentKeyVersion = table.Column<long>(type: "bigint", nullable: false),
                    RecipientAgentKeyFingerprint = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    ManifestDigest = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    EncodedPackageCiphertext = table.Column<byte[]>(type: "bytea", maxLength: 2097152, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScriptExecutionPackages", x => new { x.OrganizationId, x.VaultId, x.GrantId });
                    table.ForeignKey(
                        name: "FK_ScriptExecutionPackages_Grants_OrganizationId_VaultId_Grant~",
                        columns: x => new { x.OrganizationId, x.VaultId, x.GrantId },
                        principalTable: "Grants",
                        principalColumns: new[] { "OrganizationId", "VaultId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScriptExecutionScopes",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    VaultId = table.Column<Guid>(type: "uuid", nullable: false),
                    GrantId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntryRevision = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    IsScript = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScriptExecutionScopes", x => new { x.OrganizationId, x.VaultId, x.GrantId, x.EntryId });
                    table.ForeignKey(
                        name: "FK_ScriptExecutionScopes_Grants_OrganizationId_VaultId_GrantId",
                        columns: x => new { x.OrganizationId, x.VaultId, x.GrantId },
                        principalTable: "Grants",
                        principalColumns: new[] { "OrganizationId", "VaultId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ScriptExecutionScopes_VaultEntries_OrganizationId_VaultId_E~",
                        columns: x => new { x.OrganizationId, x.VaultId, x.EntryId },
                        principalTable: "VaultEntries",
                        principalColumns: new[] { "OrganizationId", "VaultId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Grants_OrganizationId_VaultId_ScriptEntryId_AgentId_AgentAc~",
                table: "Grants",
                columns: new[] { "OrganizationId", "VaultId", "ScriptEntryId", "AgentId", "AgentAccessEpoch", "Status" },
                unique: true,
                filter: "\"GrantType\" = 'ScriptExecution' AND \"Status\" = 2");

            migrationBuilder.CreateIndex(
                name: "IX_ScriptExecutionScopes_OrganizationId_VaultId_EntryId_GrantId",
                table: "ScriptExecutionScopes",
                columns: new[] { "OrganizationId", "VaultId", "EntryId", "GrantId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScriptExecutionPackages");

            migrationBuilder.DropTable(
                name: "ScriptExecutionScopes");

            migrationBuilder.DropIndex(
                name: "IX_Grants_OrganizationId_VaultId_ScriptEntryId_AgentId_AgentAc~",
                table: "Grants");

            migrationBuilder.DropColumn(
                name: "ScriptEntryId",
                table: "Grants");

            migrationBuilder.AlterColumn<string>(
                name: "GrantType",
                table: "Grants",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(21)",
                oldMaxLength: 21);
        }
    }
}
