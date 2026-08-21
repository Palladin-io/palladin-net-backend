using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Palladin.Module.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrganizationInvitationResend : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Instant>(
                name: "LastSentAt",
                table: "OrganizationInvitations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE "OrganizationInvitations"
                SET "LastSentAt" = "CreatedAt";
                """);

            migrationBuilder.AlterColumn<Instant>(
                name: "LastSentAt",
                table: "OrganizationInvitations",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(Instant),
                oldType: "timestamp with time zone",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastSentAt",
                table: "OrganizationInvitations");
        }
    }
}
