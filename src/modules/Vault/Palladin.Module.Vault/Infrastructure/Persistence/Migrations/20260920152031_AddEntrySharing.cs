using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Palladin.Module.Vault.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEntrySharing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EntryShareActivities",
                columns: table => new
                {
                    ShareId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    VaultId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    SenderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    NotifySender = table.Column<bool>(type: "boolean", nullable: false),
                    OccurredAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    PublishedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EntryShareActivities", x => new { x.ShareId, x.Sequence });
                });

            migrationBuilder.CreateTable(
                name: "EntryShares",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    VaultId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceRevision = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    MaximumReceipts = table.Column<int>(type: "integer", nullable: false),
                    DeliveryCount = table.Column<int>(type: "integer", nullable: false),
                    FirstDeliveredAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    LastDeliveredAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    FirstConfirmedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    NotifyOnFirstReceipt = table.Column<bool>(type: "boolean", nullable: false),
                    RecipientMode = table.Column<int>(type: "integer", nullable: false),
                    ProtectedRecipientEmail = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    Protection = table.Column<int>(type: "integer", nullable: false),
                    SecretVerifier = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    AccessTokenHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    Nonce = table.Column<byte[]>(type: "bytea", maxLength: 24, nullable: false),
                    Ciphertext = table.Column<byte[]>(type: "bytea", maxLength: 262144, nullable: false),
                    SecurityVersion = table.Column<long>(type: "bigint", nullable: false),
                    MutationVersion = table.Column<long>(type: "bigint", nullable: false),
                    ActivitySequence = table.Column<long>(type: "bigint", nullable: false),
                    RevokedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    RevocationReason = table.Column<int>(type: "integer", nullable: true),
                    ExpiredAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    FailedAttempts = table.Column<int>(type: "integer", nullable: false),
                    LockedUntil = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    LastOtpSentAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EntryShares", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EntryShares_VaultEntries_OrganizationId_VaultId_EntryId",
                        columns: x => new { x.OrganizationId, x.VaultId, x.EntryId },
                        principalTable: "VaultEntries",
                        principalColumns: new[] { "OrganizationId", "VaultId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EntryShareSessions",
                columns: table => new
                {
                    ShareId = table.Column<Guid>(type: "uuid", nullable: false),
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    SecurityVersion = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    EmailVerifiedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    SecretVerifiedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    DeliveredAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    ConfirmedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    OtpHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: true),
                    OtpExpiresAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    MutationVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EntryShareSessions", x => new { x.ShareId, x.Id });
                    table.ForeignKey(
                        name: "FK_EntryShareSessions_EntryShares_ShareId",
                        column: x => x.ShareId,
                        principalTable: "EntryShares",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EntryShareActivities_OccurredAt_ShareId_Sequence",
                table: "EntryShareActivities",
                columns: new[] { "OccurredAt", "ShareId", "Sequence" },
                filter: "\"PublishedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_EntryShares_ExpiresAt_Id",
                table: "EntryShares",
                columns: new[] { "ExpiresAt", "Id" },
                filter: "\"RevokedAt\" IS NULL AND \"ExpiredAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_EntryShares_OrganizationId_VaultId_EntryId_CreatedAt_Id",
                table: "EntryShares",
                columns: new[] { "OrganizationId", "VaultId", "EntryId", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_EntryShareSessions_ExpiresAt_ShareId_Id",
                table: "EntryShareSessions",
                columns: new[] { "ExpiresAt", "ShareId", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EntryShareActivities");

            migrationBuilder.DropTable(
                name: "EntryShareSessions");

            migrationBuilder.DropTable(
                name: "EntryShares");
        }
    }
}
