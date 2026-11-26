using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Palladin.Module.Vault.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVaultKeyRotationReasons : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VaultKeyRotationReasons",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    VaultId = table.Column<Guid>(type: "uuid", nullable: false),
                    RotationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PrincipalType = table.Column<int>(type: "integer", nullable: false),
                    PrincipalId = table.Column<Guid>(type: "uuid", nullable: false),
                    PrincipalName = table.Column<string>(type: "text", nullable: true),
                    OccurredAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VaultKeyRotationReasons", x => new { x.OrganizationId, x.VaultId, x.RotationId, x.PrincipalType, x.PrincipalId });
                    table.ForeignKey(
                        name: "FK_VaultKeyRotationReasons_VaultKeyRotations_OrganizationId_Va~",
                        columns: x => new { x.OrganizationId, x.VaultId, x.RotationId },
                        principalTable: "VaultKeyRotations",
                        principalColumns: new[] { "OrganizationId", "VaultId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VaultKeyRotationReasons");
        }
    }
}
