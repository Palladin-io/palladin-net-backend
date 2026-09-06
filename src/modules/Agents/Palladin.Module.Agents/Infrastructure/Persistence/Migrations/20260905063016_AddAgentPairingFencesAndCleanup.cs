using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Palladin.Module.Agents.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentPairingFencesAndCleanup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "revision",
                table: "agent_pairing_requests",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.CreateTable(
                name: "agent_display_name_fences",
                columns: table => new
                {
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_display_name_fences", x => x.organization_id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_agent_pairing_requests_status_updated_at_id",
                table: "agent_pairing_requests",
                columns: new[] { "status", "updated_at", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_display_name_fences");

            migrationBuilder.DropIndex(
                name: "IX_agent_pairing_requests_status_updated_at_id",
                table: "agent_pairing_requests");

            migrationBuilder.DropColumn(
                name: "revision",
                table: "agent_pairing_requests");
        }
    }
}
