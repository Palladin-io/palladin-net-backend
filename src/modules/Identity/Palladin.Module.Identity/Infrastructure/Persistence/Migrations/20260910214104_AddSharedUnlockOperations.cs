using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Palladin.Module.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSharedUnlockOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SharedUnlockOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceRefreshTokenId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceAuthorizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceSequence = table.Column<long>(type: "bigint", nullable: false),
                    SourceOrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceOfflinePolicyVersion = table.Column<long>(type: "bigint", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OfflinePolicyVersion = table.Column<long>(type: "bigint", nullable: false),
                    AuthorizationVersion = table.Column<long>(type: "bigint", nullable: false),
                    LinkId = table.Column<Guid>(type: "uuid", nullable: false),
                    LinkEpoch = table.Column<long>(type: "bigint", nullable: false),
                    PreferenceRevision = table.Column<long>(type: "bigint", nullable: false),
                    Direction = table.Column<int>(type: "integer", nullable: false),
                    ApiOrigin = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    WebOrigin = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    ExtensionId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    DocumentBinding = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    WebGeneration = table.Column<byte[]>(type: "bytea", nullable: false),
                    ExtensionGeneration = table.Column<byte[]>(type: "bytea", nullable: false),
                    SourcePublicKey = table.Column<byte[]>(type: "bytea", nullable: false),
                    RecipientPublicKey = table.Column<byte[]>(type: "bytea", nullable: false),
                    RecipientProofPublicKey = table.Column<byte[]>(type: "bytea", nullable: false),
                    Challenge = table.Column<byte[]>(type: "bytea", nullable: false),
                    KeyContextDigest = table.Column<byte[]>(type: "bytea", nullable: false),
                    TranscriptHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    IssuedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UnlockedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    IdleDeadline = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    AbsoluteDeadline = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    OfflineDeadline = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    RecipientSessionId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SharedUnlockOperations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SharedUnlockOperations_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SharedUnlockOperations_UserId",
                table: "SharedUnlockOperations",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SharedUnlockOperations");
        }
    }
}
