using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Palladin.Module.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionSecondFactorAssurance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ConfigurationRevision",
                table: "TotpCredentials",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<long>(
                name: "SecondFactorRevision",
                table: "RefreshTokens",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<Instant>(
                name: "SecondFactorVerifiedAt",
                table: "RefreshTokens",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConfigurationRevision",
                table: "TotpCredentials");

            migrationBuilder.DropColumn(
                name: "SecondFactorRevision",
                table: "RefreshTokens");

            migrationBuilder.DropColumn(
                name: "SecondFactorVerifiedAt",
                table: "RefreshTokens");
        }
    }
}
