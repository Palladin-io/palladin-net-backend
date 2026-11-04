using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Palladin.Module.Agents.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SplitAgentEncryptionKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "encryption_key_binding_signature",
                table: "agents",
                type: "bytea",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "encryption_key_suite_id",
                table: "agents",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "encryption_public_key",
                table: "agents",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<byte[]>(
                name: "previous_encryption_key_fingerprint",
                table: "agents",
                type: "bytea",
                maxLength: 32,
                nullable: true);

            // Every enrolled Agent so far encrypts to its 32-byte X25519 identity key.
            migrationBuilder.Sql(
                "UPDATE agents SET encryption_public_key = public_key, " +
                "encryption_key_suite_id = 'palladin-x25519-sealed-box-v1';");
            migrationBuilder.Sql("ALTER TABLE agents ALTER COLUMN encryption_public_key DROP DEFAULT;");
            migrationBuilder.Sql("ALTER TABLE agents ALTER COLUMN encryption_key_suite_id DROP DEFAULT;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "encryption_key_binding_signature",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "encryption_key_suite_id",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "encryption_public_key",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "previous_encryption_key_fingerprint",
                table: "agents");
        }
    }
}
