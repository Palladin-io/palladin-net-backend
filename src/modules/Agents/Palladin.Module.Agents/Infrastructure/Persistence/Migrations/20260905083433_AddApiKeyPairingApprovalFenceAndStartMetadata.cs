using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Palladin.Module.Agents.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddApiKeyPairingApprovalFenceAndStartMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Local development databases may already contain the uncommitted pairing-fence
            // column from an earlier build of this feature branch. Production migration history
            // never contained that draft migration, so the guarded DDL is equivalent on a fresh
            // database while keeping local upgrade testing non-destructive.
            migrationBuilder.Sql("""
                ALTER TABLE api_keys
                ADD COLUMN IF NOT EXISTS revision bigint NOT NULL DEFAULT 1;
                """);

            migrationBuilder.AddColumn<string>(
                name: "requested_display_name",
                table: "agent_pairing_requests",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "requested_type",
                table: "agent_pairing_requests",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE agent_pairing_requests
                SET requested_display_name = display_name,
                    requested_type = type;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "revision",
                table: "api_keys");

            migrationBuilder.DropColumn(
                name: "requested_display_name",
                table: "agent_pairing_requests");

            migrationBuilder.DropColumn(
                name: "requested_type",
                table: "agent_pairing_requests");
        }
    }
}
