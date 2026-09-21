using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Palladin.Module.Vault.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEntryShareOtpDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "OtpGeneration",
                table: "EntryShareSessions",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "OtpLanguage",
                table: "EntryShareSessions",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProtectedOtp",
                table: "EntryShareSessions",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_EntryShareSessions_OtpExpiresAt_ShareId_Id",
                table: "EntryShareSessions",
                columns: new[] { "OtpExpiresAt", "ShareId", "Id" },
                filter: "\"ProtectedOtp\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EntryShareSessions_OtpExpiresAt_ShareId_Id",
                table: "EntryShareSessions");

            migrationBuilder.DropColumn(
                name: "OtpGeneration",
                table: "EntryShareSessions");

            migrationBuilder.DropColumn(
                name: "OtpLanguage",
                table: "EntryShareSessions");

            migrationBuilder.DropColumn(
                name: "ProtectedOtp",
                table: "EntryShareSessions");
        }
    }
}
