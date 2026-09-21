using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Palladin.Module.Audit.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExplicitAuditOccurrenceIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuditLogEntries_OrganizationId_EventType_VaultId_AgentId_En~",
                table: "AuditLogEntries");

            migrationBuilder.AddColumn<bool>(
                name: "HasExplicitOccurrenceId",
                table: "AuditLogEntries",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogEntries_OrganizationId_EventType_VaultId_AgentId_En~",
                table: "AuditLogEntries",
                columns: new[] { "OrganizationId", "EventType", "VaultId", "AgentId", "EntryId", "OccurredAt" },
                unique: true,
                filter: "NOT \"HasExplicitOccurrenceId\"")
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuditLogEntries_OrganizationId_EventType_VaultId_AgentId_En~",
                table: "AuditLogEntries");

            migrationBuilder.DropColumn(
                name: "HasExplicitOccurrenceId",
                table: "AuditLogEntries");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogEntries_OrganizationId_EventType_VaultId_AgentId_En~",
                table: "AuditLogEntries",
                columns: new[] { "OrganizationId", "EventType", "VaultId", "AgentId", "EntryId", "OccurredAt" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }
    }
}
