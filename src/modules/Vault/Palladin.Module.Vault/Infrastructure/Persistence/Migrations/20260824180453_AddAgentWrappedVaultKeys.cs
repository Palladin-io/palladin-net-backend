using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Palladin.Module.Vault.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentWrappedVaultKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Pre-production protocol cutover: legacy FULL rows contain per-entry grant envelopes
            // and cannot be reinterpreted as a Vault-key membership wrapper. Remove them atomically;
            // owners must create fresh FULL grants with the canonical descriptor-bound VK wrapper.
            migrationBuilder.Sql("DELETE FROM \"Grants\" WHERE \"GrantType\" = 'Full';");

            migrationBuilder.DropTable(
                name: "FullGrantPreparationEntries");

            migrationBuilder.DropTable(
                name: "FullGrantPreparations");

            migrationBuilder.AddColumn<int>(
                name: "DeliveryPolicy",
                table: "VaultEntries",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "AgentWrappedVaultKeys",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    VaultId = table.Column<Guid>(type: "uuid", nullable: false),
                    GrantId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentAccessEpoch = table.Column<long>(type: "bigint", nullable: false),
                    ProtocolVersion = table.Column<int>(type: "integer", nullable: false),
                    WrapperSuiteId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    VaultKeyVersion = table.Column<decimal>(type: "numeric(10,0)", precision: 10, scale: 0, nullable: false),
                    RecipientAgentKeyVersion = table.Column<decimal>(type: "numeric(10,0)", precision: 10, scale: 0, nullable: false),
                    RecipientAgentKeyFingerprint = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    EncodedSealedVaultKeyPackage = table.Column<byte[]>(type: "bytea", maxLength: 120, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentWrappedVaultKeys", x => new { x.OrganizationId, x.VaultId, x.GrantId });
                    table.ForeignKey(
                        name: "FK_AgentWrappedVaultKeys_Grants_OrganizationId_VaultId_GrantId",
                        columns: x => new { x.OrganizationId, x.VaultId, x.GrantId },
                        principalTable: "Grants",
                        principalColumns: new[] { "OrganizationId", "VaultId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentWrappedVaultKeys_OrganizationId_VaultId_AgentId",
                table: "AgentWrappedVaultKeys",
                columns: new[] { "OrganizationId", "VaultId", "AgentId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentWrappedVaultKeys");

            migrationBuilder.DropColumn(
                name: "DeliveryPolicy",
                table: "VaultEntries");

            migrationBuilder.CreateTable(
                name: "FullGrantPreparations",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    VaultId = table.Column<Guid>(type: "uuid", nullable: false),
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentAccessEpoch = table.Column<long>(type: "bigint", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentKeyFingerprint = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    AgentPublicKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpirySource = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    GrantExpiresAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    MemberKeyGeneration = table.Column<long>(type: "bigint", nullable: false),
                    Methods = table.Column<int>(type: "integer", nullable: false),
                    PreparationExpiresAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    QueryLimit = table.Column<int>(type: "integer", nullable: true),
                    RecipientAgentKeyVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FullGrantPreparations", x => new { x.OrganizationId, x.VaultId, x.Id });
                    table.ForeignKey(
                        name: "FK_FullGrantPreparations_Agents_OrganizationId_AgentId",
                        columns: x => new { x.OrganizationId, x.AgentId },
                        principalTable: "Agents",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FullGrantPreparations_Vaults_OrganizationId_VaultId",
                        columns: x => new { x.OrganizationId, x.VaultId },
                        principalTable: "Vaults",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FullGrantPreparationEntries",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    VaultId = table.Column<Guid>(type: "uuid", nullable: false),
                    PreparationId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntryRevision = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    Payload = table.Column<byte[]>(type: "bytea", maxLength: 393216, nullable: false),
                    PayloadDigest = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    PreparedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FullGrantPreparationEntries", x => new { x.OrganizationId, x.VaultId, x.PreparationId, x.EntryId });
                    table.ForeignKey(
                        name: "FK_FullGrantPreparationEntries_FullGrantPreparations_Organizat~",
                        columns: x => new { x.OrganizationId, x.VaultId, x.PreparationId },
                        principalTable: "FullGrantPreparations",
                        principalColumns: new[] { "OrganizationId", "VaultId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FullGrantPreparations_Id",
                table: "FullGrantPreparations",
                column: "Id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FullGrantPreparations_OrganizationId_AgentId",
                table: "FullGrantPreparations",
                columns: new[] { "OrganizationId", "AgentId" });

            migrationBuilder.CreateIndex(
                name: "IX_FullGrantPreparations_OrganizationId_VaultId_AgentId",
                table: "FullGrantPreparations",
                columns: new[] { "OrganizationId", "VaultId", "AgentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FullGrantPreparations_PreparationExpiresAt",
                table: "FullGrantPreparations",
                column: "PreparationExpiresAt");
        }
    }
}
