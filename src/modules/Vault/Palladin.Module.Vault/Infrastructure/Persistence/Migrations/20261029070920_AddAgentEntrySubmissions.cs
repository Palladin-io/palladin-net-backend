using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Palladin.Module.Vault.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentEntrySubmissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentEntrySubmissions",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    EntryType = table.Column<int>(type: "integer", nullable: false),
                    SuggestedVaultId = table.Column<Guid>(type: "uuid", nullable: true),
                    TargetVaultId = table.Column<Guid>(type: "uuid", nullable: true),
                    TargetEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    ExpectedEntryRevision = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: true),
                    State = table.Column<int>(type: "integer", nullable: false),
                    AgentUseSuspended = table.Column<bool>(type: "boolean", nullable: false),
                    SubmissionRevision = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    AgentAccessEpoch = table.Column<long>(type: "bigint", nullable: false),
                    ApproverKeyVersion = table.Column<long>(type: "bigint", nullable: false),
                    Document = table.Column<string>(type: "character varying(49152)", maxLength: 49152, nullable: true),
                    SubmittedDigest = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    TrashedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    DecidedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    DecidedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    DecisionVaultId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecisionEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecisionGrantId = table.Column<Guid>(type: "uuid", nullable: true),
                    Version = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentEntrySubmissions", x => new { x.OrganizationId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "ApproverKeyChallenges",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    IssuedTo = table.Column<Guid>(type: "uuid", nullable: false),
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IssuedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ApproverSetRevision = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    ApproverSetDigest = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApproverKeyChallenges", x => new { x.OrganizationId, x.IssuedTo });
                });

            migrationBuilder.CreateTable(
                name: "OrganizationApproverKeySets",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    KeyVersion = table.Column<long>(type: "bigint", nullable: false),
                    ManifestRevision = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    ApproverSetRevision = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    MessagePublicKey = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    MessageKeyFingerprint = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    SigningPublicKey = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    SigningKeyFingerprint = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    PreviousSigningKeyFingerprint = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: true),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizationApproverKeySets", x => x.OrganizationId);
                });

            migrationBuilder.CreateTable(
                name: "OrganizationApprovers",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsApprover = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizationApprovers", x => new { x.OrganizationId, x.UserId });
                });

            migrationBuilder.CreateTable(
                name: "ApproverKeyWraps",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    MemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    Purpose = table.Column<int>(type: "integer", nullable: false),
                    KeyVersion = table.Column<long>(type: "bigint", nullable: false),
                    ApproverSetRevision = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    RecipientKeyVersion = table.Column<decimal>(type: "numeric(10,0)", precision: 10, scale: 0, nullable: false),
                    RecipientFingerprint = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    SealedPackage = table.Column<byte[]>(type: "bytea", maxLength: 120, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApproverKeyWraps", x => new { x.OrganizationId, x.MemberId, x.KeyVersion, x.Purpose });
                    table.ForeignKey(
                        name: "FK_ApproverKeyWraps_OrganizationApproverKeySets_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "OrganizationApproverKeySets",
                        principalColumn: "OrganizationId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OrganizationApproverManifests",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ManifestRevision = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    KeyVersion = table.Column<long>(type: "bigint", nullable: false),
                    Document = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizationApproverManifests", x => new { x.OrganizationId, x.ManifestRevision });
                    table.ForeignKey(
                        name: "FK_OrganizationApproverManifests_OrganizationApproverKeySets_O~",
                        column: x => x.OrganizationId,
                        principalTable: "OrganizationApproverKeySets",
                        principalColumn: "OrganizationId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentEntrySubmissions_OrganizationId_AgentId_CreatedAt",
                table: "AgentEntrySubmissions",
                columns: new[] { "OrganizationId", "AgentId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentEntrySubmissions_OrganizationId_State_CreatedAt",
                table: "AgentEntrySubmissions",
                columns: new[] { "OrganizationId", "State", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentEntrySubmissions_TrashedAt",
                table: "AgentEntrySubmissions",
                column: "TrashedAt",
                filter: "\"State\" = 3");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentEntrySubmissions");

            migrationBuilder.DropTable(
                name: "ApproverKeyChallenges");

            migrationBuilder.DropTable(
                name: "ApproverKeyWraps");

            migrationBuilder.DropTable(
                name: "OrganizationApproverManifests");

            migrationBuilder.DropTable(
                name: "OrganizationApprovers");

            migrationBuilder.DropTable(
                name: "OrganizationApproverKeySets");
        }
    }
}
