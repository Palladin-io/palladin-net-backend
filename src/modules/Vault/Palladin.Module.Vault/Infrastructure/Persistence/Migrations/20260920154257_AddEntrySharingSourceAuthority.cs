using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Palladin.Module.Vault.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEntrySharingSourceAuthority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SharingDisabled",
                table: "VaultOrganizationLifecycles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "SharingRevokedThroughRevision",
                table: "VaultEntries",
                type: "numeric(20,0)",
                precision: 20,
                scale: 0,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<long>(
                name: "SenderAuthorizationVersion",
                table: "EntryShares",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<Instant>(
                name: "SenderVaultMembershipAddedAt",
                table: "EntryShares",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: NodaTime.Instant.FromUnixTimeTicks(0L));

            migrationBuilder.CreateTable(
                name: "EntryShareCreationChallenges",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    VaultId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    ShareId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpiresAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    MutationVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EntryShareCreationChallenges", x => new { x.OrganizationId, x.VaultId, x.EntryId, x.RequestedBy });
                    table.ForeignKey(
                        name: "FK_EntryShareCreationChallenges_VaultEntries_OrganizationId_Va~",
                        columns: x => new { x.OrganizationId, x.VaultId, x.EntryId },
                        principalTable: "VaultEntries",
                        principalColumns: new[] { "OrganizationId", "VaultId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EntryShareSenderAuthorities",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RevokedThroughAuthorizationVersion = table.Column<long>(type: "bigint", nullable: false),
                    MutationVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EntryShareSenderAuthorities", x => new { x.OrganizationId, x.UserId });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EntryShareCreationChallenges");

            migrationBuilder.DropTable(
                name: "EntryShareSenderAuthorities");

            migrationBuilder.DropColumn(
                name: "SharingDisabled",
                table: "VaultOrganizationLifecycles");

            migrationBuilder.DropColumn(
                name: "SharingRevokedThroughRevision",
                table: "VaultEntries");

            migrationBuilder.DropColumn(
                name: "SenderAuthorizationVersion",
                table: "EntryShares");

            migrationBuilder.DropColumn(
                name: "SenderVaultMembershipAddedAt",
                table: "EntryShares");
        }
    }
}
