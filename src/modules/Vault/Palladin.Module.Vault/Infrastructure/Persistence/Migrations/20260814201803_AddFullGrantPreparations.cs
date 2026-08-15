using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Palladin.Module.Vault.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFullGrantPreparations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FullGrantPreparations",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    VaultId = table.Column<Guid>(type: "uuid", nullable: false),
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentAccessEpoch = table.Column<long>(type: "bigint", nullable: false),
                    AgentPublicKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    RecipientAgentKeyVersion = table.Column<long>(type: "bigint", nullable: false),
                    AgentKeyFingerprint = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    MemberKeyGeneration = table.Column<long>(type: "bigint", nullable: false),
                    Methods = table.Column<int>(type: "integer", nullable: false),
                    GrantExpiresAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    QueryLimit = table.Column<int>(type: "integer", nullable: true),
                    ExpirySource = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    PreparationExpiresAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FullGrantPreparationEntries");

            migrationBuilder.DropTable(
                name: "FullGrantPreparations");
        }
    }
}
