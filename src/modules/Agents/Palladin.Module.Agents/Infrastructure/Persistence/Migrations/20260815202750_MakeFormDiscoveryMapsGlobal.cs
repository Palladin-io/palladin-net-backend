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
                -- The retired organization-scoped endpoint accepted caller-supplied fingerprints
                -- without binding them to the typed definition. Those rows cannot become trusted
                -- global candidates, so this pre-production cutover intentionally resets them.
                DELETE FROM form_discovery_maps;

                CREATE SEQUENCE form_discovery_map_revision_seq AS integer START WITH 1;
                ALTER SEQUENCE form_discovery_map_revision_seq
                    OWNED BY form_discovery_maps.map_version;
                ALTER TABLE form_discovery_maps
                    ALTER COLUMN map_version
                    SET DEFAULT nextval('form_discovery_map_revision_seq');
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
                name: "IX_form_discovery_maps_domain_provider_fingerprint",
                table: "form_discovery_maps",
                columns: new[] { "domain", "provider", "fingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_form_discovery_maps_domain_provider_status_map_version",
                table: "form_discovery_maps",
                columns: new[] { "domain", "provider", "status", "map_version" });

            migrationBuilder.CreateIndex(
                name: "IX_form_discovery_maps_map_version",
                table: "form_discovery_maps",
                column: "map_version",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $migration$
                BEGIN
                    RAISE EXCEPTION 'MakeFormDiscoveryMapsGlobal is an irreversible pre-production cutover';
                END
                $migration$;
                """);
        }
    }
}
