using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Palladin.Module.Agents.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPairingConnectionInfo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "hostname",
                table: "agent_pairing_requests",
                type: "character varying(253)",
                maxLength: 253,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ip",
                table: "agent_pairing_requests",
                type: "character varying(45)",
                maxLength: 45,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "hostname",
                table: "agent_pairing_requests");

            migrationBuilder.DropColumn(
                name: "ip",
                table: "agent_pairing_requests");
        }
    }
}
