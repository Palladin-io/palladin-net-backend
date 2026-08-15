using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Palladin.Module.Agents.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MakeFormDiscoveryMapsGlobal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP INDEX IF EXISTS "IX_form_discovery_maps_domain_provider_map_version";
                DROP INDEX IF EXISTS "IX_form_discovery_maps_organization_id_domain_provider_map_ver~";
                DROP INDEX IF EXISTS "IX_form_discovery_maps_organization_id_domain_provider_status";
                DROP INDEX IF EXISTS "IX_form_discovery_maps_scope_organization_id_domain_provider_s~";
                """);

            migrationBuilder.Sql(
                """
                WITH ranked_maps AS (
                    SELECT id,
                           (ROW_NUMBER() OVER (
                               PARTITION BY domain, provider
                               ORDER BY map_version, created_at, id))::integer AS global_version
                    FROM form_discovery_maps
                )
                UPDATE form_discovery_maps AS maps
                SET map_version = ranked_maps.global_version,
                    status = 0
                FROM ranked_maps
                WHERE maps.id = ranked_maps.id;
                """);

            migrationBuilder.Sql(
                "ALTER TABLE form_discovery_maps DROP COLUMN IF EXISTS scope;");

            migrationBuilder.DropColumn(
                name: "organization_id",
                table: "form_discovery_maps");

            migrationBuilder.AlterColumn<string>(
                name: "provider",
                table: "form_discovery_maps",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64);

            migrationBuilder.AlterColumn<string>(
                name: "login_url",
                table: "form_discovery_maps",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(2048)",
                oldMaxLength: 2048);

            migrationBuilder.AlterColumn<string>(
                name: "fingerprint",
                table: "form_discovery_maps",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128);

            migrationBuilder.AlterColumn<string>(
                name: "definition_json",
                table: "form_discovery_maps",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(65536)",
                oldMaxLength: 65536);

            migrationBuilder.CreateIndex(
                name: "IX_form_discovery_maps_domain_provider_map_version",
                table: "form_discovery_maps",
                columns: new[] { "domain", "provider", "map_version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_form_discovery_maps_domain_provider_status_map_version",
                table: "form_discovery_maps",
                columns: new[] { "domain", "provider", "status", "map_version" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_form_discovery_maps_domain_provider_map_version",
                table: "form_discovery_maps");

            migrationBuilder.DropIndex(
                name: "IX_form_discovery_maps_domain_provider_status_map_version",
                table: "form_discovery_maps");

            migrationBuilder.AlterColumn<string>(
                name: "provider",
                table: "form_discovery_maps",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "login_url",
                table: "form_discovery_maps",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "fingerprint",
                table: "form_discovery_maps",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64);

            migrationBuilder.AlterColumn<string>(
                name: "definition_json",
                table: "form_discovery_maps",
                type: "character varying(65536)",
                maxLength: 65536,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.Sql(
                "DELETE FROM form_discovery_maps;");

            migrationBuilder.AddColumn<Guid>(
                name: "organization_id",
                table: "form_discovery_maps",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

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
    }
}
