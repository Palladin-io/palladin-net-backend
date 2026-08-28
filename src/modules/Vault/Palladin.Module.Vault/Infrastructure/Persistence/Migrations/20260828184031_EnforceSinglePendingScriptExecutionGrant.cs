using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Palladin.Module.Vault.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnforceSinglePendingScriptExecutionGrant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Grants_ScriptExecution_Pending",
                table: "Grants",
                columns: new[] { "OrganizationId", "VaultId", "ScriptEntryId", "AgentId", "AgentAccessEpoch", "Status" },
                unique: true,
                filter: "\"GrantType\" = 'ScriptExecution' AND \"Status\" = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Grants_ScriptExecution_Pending",
                table: "Grants");
        }
    }
}
