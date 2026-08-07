using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Palladin.Module.Audit.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditExportJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    VaultIds = table.Column<string>(type: "text", nullable: true),
                    AgentIds = table.Column<string>(type: "text", nullable: true),
                    UserIds = table.Column<string>(type: "text", nullable: true),
                    EntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    EventTypes = table.Column<string>(type: "text", nullable: true),
                    From = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    To = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    StorageKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    RowCount = table.Column<int>(type: "integer", nullable: true),
                    Error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    ExpiresAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditExportJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuditLogEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ActorType = table.Column<int>(type: "integer", nullable: false),
                    Result = table.Column<short>(type: "smallint", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: true),
                    VaultId = table.Column<Guid>(type: "uuid", nullable: true),
                    EntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    AgentName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ActorName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    IpAddress = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    Metadata = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    OccurredAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditExportJobs_OrganizationId_Id",
                table: "AuditExportJobs",
                columns: new[] { "OrganizationId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogEntries_OrganizationId_CreatedAt_Id",
                table: "AuditLogEntries",
                columns: new[] { "OrganizationId", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogEntries_OrganizationId_EventType_VaultId_AgentId_En~",
                table: "AuditLogEntries",
                columns: new[] { "OrganizationId", "EventType", "VaultId", "AgentId", "EntryId", "OccurredAt" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogEntries_VaultId_CreatedAt_Id",
                table: "AuditLogEntries",
                columns: new[] { "VaultId", "CreatedAt", "Id" });

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditExportJobs");

            migrationBuilder.DropTable(
                name: "AuditLogEntries");
        }
    }
}
