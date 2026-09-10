using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Palladin.Module.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSharedUnlockLogoutBarrier : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "LastLogoutSequence",
                table: "SharedUnlockLinks",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastLogoutSequence",
                table: "SharedUnlockLinks");
        }
    }
}
