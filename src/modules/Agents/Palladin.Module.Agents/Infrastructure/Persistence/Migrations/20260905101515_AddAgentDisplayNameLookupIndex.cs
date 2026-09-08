using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Palladin.Module.Agents.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentDisplayNameLookupIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE INDEX "IX_agents_organization_id_upper_name"
                ON agents (organization_id, upper(name))
                WHERE name IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_agents_organization_id_upper_name\";");
        }
    }
}
