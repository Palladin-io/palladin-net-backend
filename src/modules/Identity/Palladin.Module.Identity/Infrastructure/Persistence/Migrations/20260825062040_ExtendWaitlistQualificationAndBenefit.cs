using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Palladin.Module.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExtendWaitlistQualificationAndBenefit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AgentFramework",
                table: "WaitlistEntries",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AgentFrameworkOther",
                table: "WaitlistEntries",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AudienceType",
                table: "WaitlistEntries",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<Instant>(
                name: "BenefitEligibleAt",
                table: "WaitlistEntries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Instant>(
                name: "BenefitEndsAt",
                table: "WaitlistEntries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BenefitPlan",
                table: "WaitlistEntries",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<Instant>(
                name: "BenefitStartsAt",
                table: "WaitlistEntries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BenefitStatus",
                table: "WaitlistEntries",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BenefitUserId",
                table: "WaitlistEntries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CampaignSource",
                table: "WaitlistEntries",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CredentialedWorkflow",
                table: "WaitlistEntries",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CurrentWorkaround",
                table: "WaitlistEntries",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<Instant>(
                name: "PromotionTermsAcceptedAt",
                table: "WaitlistEntries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PromotionTermsVersion",
                table: "WaitlistEntries",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ReadyWithin30Days",
                table: "WaitlistEntries",
                type: "boolean",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WaitlistEntries_BenefitUserId",
                table: "WaitlistEntries",
                column: "BenefitUserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WaitlistEntries_BenefitUserId",
                table: "WaitlistEntries");

            migrationBuilder.DropColumn(
                name: "AgentFramework",
                table: "WaitlistEntries");

            migrationBuilder.DropColumn(
                name: "AgentFrameworkOther",
                table: "WaitlistEntries");

            migrationBuilder.DropColumn(
                name: "AudienceType",
                table: "WaitlistEntries");

            migrationBuilder.DropColumn(
                name: "BenefitEligibleAt",
                table: "WaitlistEntries");

            migrationBuilder.DropColumn(
                name: "BenefitEndsAt",
                table: "WaitlistEntries");

            migrationBuilder.DropColumn(
                name: "BenefitPlan",
                table: "WaitlistEntries");

            migrationBuilder.DropColumn(
                name: "BenefitStartsAt",
                table: "WaitlistEntries");

            migrationBuilder.DropColumn(
                name: "BenefitStatus",
                table: "WaitlistEntries");

            migrationBuilder.DropColumn(
                name: "BenefitUserId",
                table: "WaitlistEntries");

            migrationBuilder.DropColumn(
                name: "CampaignSource",
                table: "WaitlistEntries");

            migrationBuilder.DropColumn(
                name: "CredentialedWorkflow",
                table: "WaitlistEntries");

            migrationBuilder.DropColumn(
                name: "CurrentWorkaround",
                table: "WaitlistEntries");

            migrationBuilder.DropColumn(
                name: "PromotionTermsAcceptedAt",
                table: "WaitlistEntries");

            migrationBuilder.DropColumn(
                name: "PromotionTermsVersion",
                table: "WaitlistEntries");

            migrationBuilder.DropColumn(
                name: "ReadyWithin30Days",
                table: "WaitlistEntries");
        }
    }
}
