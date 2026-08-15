using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Palladin.Module.Agents.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFormDiscoveryMaps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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

            migrationBuilder.CreateIndex(
                name: "IX_form_discovery_maps_organization_id_domain_provider_map_ver~",
                table: "form_discovery_maps",
                columns: new[] { "organization_id", "domain", "provider", "map_version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_form_discovery_maps_organization_id_domain_provider_status",
                table: "form_discovery_maps",
                columns: new[] { "organization_id", "domain", "provider", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "form_discovery_maps");
        }
    }
}
