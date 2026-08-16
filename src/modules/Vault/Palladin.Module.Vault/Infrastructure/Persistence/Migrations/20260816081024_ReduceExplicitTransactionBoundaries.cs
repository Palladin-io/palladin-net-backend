using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Palladin.Module.Vault.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReduceExplicitTransactionBoundaries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Pairing was removed before production. Some local/staging databases were
            // provisioned from an intermediate model that never contained these tables,
            // so the destructive cutover must tolerate both starting schemas.
            migrationBuilder.Sql("DROP TABLE IF EXISTS \"AgentPairingActivationCandidates\";");
            migrationBuilder.Sql("DROP TABLE IF EXISTS \"AgentPairingActivations\";");

            migrationBuilder.AddColumn<Instant>(
                name: "DeletionRequestedAt",
                table: "Vaults",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DeletionRequestedBy",
                table: "Vaults",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletionRequestedByName",
                table: "Vaults",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleting",
                table: "Vaults",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "MutationVersion",
                table: "Vaults",
                type: "numeric(20,0)",
                precision: 20,
                scale: 0,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<bool>(
                name: "IsPurging",
                table: "VaultEntries",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "PurgeLedgerRequired",
                table: "VaultEntries",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Instant>(
                name: "PurgeRequestedAt",
                table: "VaultEntries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PurgeRequestedBy",
                table: "VaultEntries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MutationVersion",
                table: "Agents",
                type: "numeric(20,0)",
                precision: 20,
                scale: 0,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "VaultOrganizationLifecycles",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    MutationVersion = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VaultOrganizationLifecycles", x => x.OrganizationId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VaultPrincipalDeprovisionings_OrganizationId",
                table: "VaultPrincipalDeprovisionings",
                column: "OrganizationId",
                unique: true,
                filter: "\"Status\" = 2");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VaultOrganizationLifecycles");

            migrationBuilder.DropIndex(
                name: "IX_VaultPrincipalDeprovisionings_OrganizationId",
                table: "VaultPrincipalDeprovisionings");

            migrationBuilder.DropColumn(
                name: "DeletionRequestedAt",
                table: "Vaults");

            migrationBuilder.DropColumn(
                name: "DeletionRequestedBy",
                table: "Vaults");

            migrationBuilder.DropColumn(
                name: "DeletionRequestedByName",
                table: "Vaults");

            migrationBuilder.DropColumn(
                name: "IsDeleting",
                table: "Vaults");

            migrationBuilder.DropColumn(
                name: "MutationVersion",
                table: "Vaults");

            migrationBuilder.DropColumn(
                name: "IsPurging",
                table: "VaultEntries");

            migrationBuilder.DropColumn(
                name: "PurgeLedgerRequired",
                table: "VaultEntries");

            migrationBuilder.DropColumn(
                name: "PurgeRequestedAt",
                table: "VaultEntries");

            migrationBuilder.DropColumn(
                name: "PurgeRequestedBy",
                table: "VaultEntries");

            migrationBuilder.DropColumn(
                name: "MutationVersion",
                table: "Agents");

            migrationBuilder.CreateTable(
                name: "AgentPairingActivations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentAccessEpoch = table.Column<long>(type: "bigint", nullable: false),
                    AgentEd25519Fingerprint = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentX25519Fingerprint = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    CandidateDigest = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    CandidateVaultCount = table.Column<int>(type: "integer", nullable: false),
                    ConfirmedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    ConfirmedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentPairingActivations", x => x.Id);
                    table.UniqueConstraint("AK_AgentPairingActivations_Id_OrganizationId_AgentId", x => new { x.Id, x.OrganizationId, x.AgentId });
                    table.ForeignKey(
                        name: "FK_AgentPairingActivations_Agents_OrganizationId_AgentId",
                        columns: x => new { x.OrganizationId, x.AgentId },
                        principalTable: "Agents",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AgentPairingActivationCandidates",
                columns: table => new
                {
                    ActivationId = table.Column<Guid>(type: "uuid", nullable: false),
                    VaultId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ManifestRevision = table.Column<decimal>(type: "numeric(20,0)", precision: 20, nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SignedManifestDigest = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    VaultSigningKeyFingerprint = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentPairingActivationCandidates", x => new { x.ActivationId, x.VaultId });
                    table.ForeignKey(
                        name: "FK_AgentPairingActivationCandidates_AgentPairingActivations_Ac~",
                        columns: x => new { x.ActivationId, x.OrganizationId, x.AgentId },
                        principalTable: "AgentPairingActivations",
                        principalColumns: new[] { "Id", "OrganizationId", "AgentId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AgentPairingActivationCandidates_Vaults_OrganizationId_Vaul~",
                        columns: x => new { x.OrganizationId, x.VaultId },
                        principalTable: "Vaults",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentPairingActivationCandidates_ActivationId_OrganizationI~",
                table: "AgentPairingActivationCandidates",
                columns: new[] { "ActivationId", "OrganizationId", "AgentId" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentPairingActivationCandidates_OrganizationId_AgentId_Vau~",
                table: "AgentPairingActivationCandidates",
                columns: new[] { "OrganizationId", "AgentId", "VaultId", "ManifestRevision" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentPairingActivationCandidates_OrganizationId_VaultId",
                table: "AgentPairingActivationCandidates",
                columns: new[] { "OrganizationId", "VaultId" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentPairingActivations_ExpiresAt",
                table: "AgentPairingActivations",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_AgentPairingActivations_OrganizationId_AgentId_AgentAccessE~",
                table: "AgentPairingActivations",
                columns: new[] { "OrganizationId", "AgentId", "AgentAccessEpoch", "CreatedAt" });
        }
    }
}
