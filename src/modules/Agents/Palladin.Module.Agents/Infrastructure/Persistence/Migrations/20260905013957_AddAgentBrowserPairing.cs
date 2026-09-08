using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Palladin.Module.Agents.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentBrowserPairing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "type",
                table: "agents",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100);

            migrationBuilder.CreateTable(
                name: "agent_pairing_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    public_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    signing_public_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    display_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    reserved_display_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    reserved_display_name_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    agent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    api_key_id = table.Column<Guid>(type: "uuid", nullable: true),
                    credential_suite = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    credential_ephemeral_public_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    credential_nonce = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: true),
                    credential_ciphertext = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_pairing_requests", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "api_key_credentials",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    api_key_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key_hash = table.Column<string>(type: "text", nullable: false),
                    key_suffix = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_key_credentials", x => x.id);
                    table.ForeignKey(
                        name: "fk_api_key_credentials_agent",
                        column: x => x.agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_api_key_credentials_api_key",
                        column: x => x.api_key_id,
                        principalTable: "api_keys",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_agent_pairing_requests_expires_at",
                table: "agent_pairing_requests",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_agent_pairing_requests_public_key",
                table: "agent_pairing_requests",
                column: "public_key");

            migrationBuilder.CreateIndex(
                name: "ux_agent_pairing_requests_reserved_name",
                table: "agent_pairing_requests",
                columns: new[] { "organization_id", "reserved_display_name_key" },
                unique: true,
                filter: "status = 1 AND reserved_display_name_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_api_key_credentials_agent_id",
                table: "api_key_credentials",
                column: "agent_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_api_key_credentials_api_key_id",
                table: "api_key_credentials",
                column: "api_key_id");

            migrationBuilder.CreateIndex(
                name: "IX_api_key_credentials_key_hash",
                table: "api_key_credentials",
                column: "key_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_pairing_requests");

            migrationBuilder.DropTable(
                name: "api_key_credentials");

            migrationBuilder.Sql("""
                UPDATE agents
                SET type = 'Unknown'
                WHERE type IS NULL;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "type",
                table: "agents",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100,
                oldNullable: true);
        }
    }
}
