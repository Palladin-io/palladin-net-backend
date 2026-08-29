using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Palladin.Module.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrganizationOfflineAccessPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "OfflineAccessPolicy",
                table: "Organizations",
                type: "integer",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<long>(
                name: "OfflineAccessPolicyVersion",
                table: "Organizations",
                type: "bigint",
                precision: 10,
                scale: 0,
                nullable: false,
                defaultValue: 1L);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OfflineAccessPolicy",
                table: "Organizations");

            migrationBuilder.DropColumn(
                name: "OfflineAccessPolicyVersion",
                table: "Organizations");
        }
    }
}
