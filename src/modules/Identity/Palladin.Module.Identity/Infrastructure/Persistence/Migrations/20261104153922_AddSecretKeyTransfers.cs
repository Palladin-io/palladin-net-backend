using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Palladin.Module.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSecretKeyTransfers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SecretKeyTransfers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Flow = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    Challenge = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    PollTokenHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: true),
                    RecipientPublicKey = table.Column<byte[]>(type: "bytea", maxLength: 1216, nullable: true),
                    SenderUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    KemCiphertext = table.Column<byte[]>(type: "bytea", maxLength: 1120, nullable: true),
                    SealedSecret = table.Column<byte[]>(type: "bytea", maxLength: 48, nullable: true),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecretKeyTransfers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SecretKeyTransfers_Users_SenderUserId",
                        column: x => x.SenderUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SecretKeyTransfers_ExpiresAt_Id",
                table: "SecretKeyTransfers",
                columns: new[] { "ExpiresAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_SecretKeyTransfers_SenderUserId",
                table: "SecretKeyTransfers",
                column: "SenderUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SecretKeyTransfers");
        }
    }
}
