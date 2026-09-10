using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Palladin.Module.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSharedUnlockAuthorizations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "SharedUnlockSequence",
                table: "Users",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "LastInvalidationSequence",
                table: "SharedUnlockLinks",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<Guid>(
                name: "SessionId",
                table: "RefreshTokens",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SharedUnlockAuthorizations",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    LinkId = table.Column<Guid>(type: "uuid", nullable: true),
                    LinkEpoch = table.Column<long>(type: "bigint", nullable: true),
                    SourceGeneration = table.Column<byte[]>(type: "bytea", nullable: false),
                    CredentialRevision = table.Column<long>(type: "bigint", nullable: false),
                    PrivateKeyWrapRevision = table.Column<long>(type: "bigint", nullable: false),
                    AuthorizationVersion = table.Column<long>(type: "bigint", nullable: false),
                    SecondFactorRevision = table.Column<long>(type: "bigint", nullable: true),
                    SecondFactorVerifiedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    UnlockedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    IdleDeadline = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    AbsoluteDeadline = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    OfflineDeadline = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SharedUnlockAuthorizations", x => new { x.UserId, x.SessionId });
                    table.ForeignKey(
                        name: "FK_SharedUnlockAuthorizations_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SharedUnlockAuthorizations");

            migrationBuilder.DropColumn(
                name: "SharedUnlockSequence",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "LastInvalidationSequence",
                table: "SharedUnlockLinks");

            migrationBuilder.DropColumn(
                name: "SessionId",
                table: "RefreshTokens");
        }
    }
}
