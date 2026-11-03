using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Palladin.Module.Agents.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDiscardedUsers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_discarded_users",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    discarded_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_discarded_users", x => x.user_id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_discarded_users");
        }
    }
}
