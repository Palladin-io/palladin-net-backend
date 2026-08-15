using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Palladin.Module.Agents.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "api_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key_hash = table.Column<string>(type: "text", nullable: false),
                    key_suffix = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    revoked_by = table.Column<Guid>(type: "uuid", nullable: true),
                    revoked_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_keys", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "form_discovery_maps",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    submitted_by_agent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    domain = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    login_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    map_version = table.Column<int>(type: "integer", nullable: false),
                    definition_json = table.Column<string>(type: "character varying(65536)", maxLength: 65536, nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_form_discovery_maps", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "agents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    public_key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    signing_public_key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    recipient_key_version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    access_epoch = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    icon_key = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    icon_color = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    last_used_api_key_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_access_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    last_ip = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    last_hostname = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: true),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    enrolled_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    enrolled_by = table.Column<Guid>(type: "uuid", nullable: true),
                    deactivated_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    deactivated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    deactivation_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reactivated_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    reactivated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agents", x => x.id);
                    table.ForeignKey(
                        name: "fk_agents_deactivated_by",
                        column: x => x.deactivated_by,
                        principalTable: "agent_users",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_agents_enrolled_by",
                        column: x => x.enrolled_by,
                        principalTable: "agent_users",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_agents_reactivated_by",
                        column: x => x.reactivated_by,
                        principalTable: "agent_users",
                        principalColumn: "id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_agents_deactivated_by",
                table: "agents",
                column: "deactivated_by");

            migrationBuilder.CreateIndex(
                name: "IX_agents_enrolled_by",
                table: "agents",
                column: "enrolled_by");

            migrationBuilder.CreateIndex(
                name: "IX_agents_public_key",
                table: "agents",
                column: "public_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_agents_reactivated_by",
                table: "agents",
                column: "reactivated_by");

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_key_hash",
                table: "api_keys",
                column: "key_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_organization_id",
                table: "api_keys",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_form_discovery_maps_organization_id_domain_provider_status",
                table: "form_discovery_maps",
                columns: new[] { "organization_id", "domain", "provider", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agents");

            migrationBuilder.DropTable(
                name: "api_keys");

            migrationBuilder.DropTable(
                name: "form_discovery_maps");

            migrationBuilder.DropTable(
                name: "agent_users");
        }
    }
}
