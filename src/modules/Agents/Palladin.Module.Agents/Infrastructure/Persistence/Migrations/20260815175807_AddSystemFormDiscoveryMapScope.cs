using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Palladin.Module.Agents.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSystemFormDiscoveryMapScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_form_discovery_maps_organization_id_domain_provider_map_ver~",
                table: "form_discovery_maps");

            migrationBuilder.DropIndex(
                name: "IX_form_discovery_maps_organization_id_domain_provider_status",
                table: "form_discovery_maps");

            migrationBuilder.AlterColumn<Guid>(
                name: "organization_id",
                table: "form_discovery_maps",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<int>(
                name: "scope",
                table: "form_discovery_maps",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.Sql(
                "ALTER TABLE form_discovery_maps ALTER COLUMN scope DROP DEFAULT;");

            migrationBuilder.CreateIndex(
                name: "IX_form_discovery_maps_domain_provider_map_version",
                table: "form_discovery_maps",
                columns: new[] { "domain", "provider", "map_version" },
                unique: true,
                filter: "\"scope\" = 0");

            migrationBuilder.CreateIndex(
                name: "IX_form_discovery_maps_organization_id_domain_provider_map_ver~",
                table: "form_discovery_maps",
                columns: new[] { "organization_id", "domain", "provider", "map_version" },
                unique: true,
                filter: "\"scope\" = 1");

            migrationBuilder.CreateIndex(
                name: "IX_form_discovery_maps_scope_organization_id_domain_provider_s~",
                table: "form_discovery_maps",
                columns: new[] { "scope", "organization_id", "domain", "provider", "status", "map_version" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DELETE FROM form_discovery_maps WHERE scope = 0;");

            migrationBuilder.DropIndex(
                name: "IX_form_discovery_maps_domain_provider_map_version",
                table: "form_discovery_maps");

            migrationBuilder.DropIndex(
                name: "IX_form_discovery_maps_organization_id_domain_provider_map_ver~",
                table: "form_discovery_maps");

            migrationBuilder.DropIndex(
                name: "IX_form_discovery_maps_scope_organization_id_domain_provider_s~",
                table: "form_discovery_maps");

            migrationBuilder.DropColumn(
                name: "scope",
                table: "form_discovery_maps");

            migrationBuilder.AlterColumn<Guid>(
                name: "organization_id",
                table: "form_discovery_maps",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

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
