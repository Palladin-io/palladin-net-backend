using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Palladin.Module.Vault.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVaultKeyRotationCommittedBy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CommittedBy",
                table: "VaultKeyRotations",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CommittedBy",
                table: "VaultKeyRotations");
        }
    }
}
