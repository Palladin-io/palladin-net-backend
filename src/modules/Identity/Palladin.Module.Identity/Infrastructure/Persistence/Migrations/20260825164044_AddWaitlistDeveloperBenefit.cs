using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Palladin.Module.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWaitlistDeveloperBenefit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Instant>(
                name: "DeveloperBenefitEndsAt",
                table: "WaitlistEntries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Instant>(
                name: "DeveloperBenefitStartedAt",
                table: "WaitlistEntries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DeveloperBenefitUserId",
                table: "WaitlistEntries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Instant>(
                name: "WaitlistDeveloperBenefitEndsAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Instant>(
                name: "WaitlistDeveloperBenefitStartedAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WaitlistEntries_DeveloperBenefitUserId",
                table: "WaitlistEntries",
                column: "DeveloperBenefitUserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WaitlistEntries_DeveloperBenefitUserId",
                table: "WaitlistEntries");

            migrationBuilder.DropColumn(
                name: "DeveloperBenefitEndsAt",
                table: "WaitlistEntries");

            migrationBuilder.DropColumn(
                name: "DeveloperBenefitStartedAt",
                table: "WaitlistEntries");

            migrationBuilder.DropColumn(
                name: "DeveloperBenefitUserId",
                table: "WaitlistEntries");

            migrationBuilder.DropColumn(
                name: "WaitlistDeveloperBenefitEndsAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "WaitlistDeveloperBenefitStartedAt",
                table: "Users");
        }
    }
}
