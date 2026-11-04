using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Palladin.Module.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountSecretSecurityUpgrade : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "LastSecurityUpgradeId",
                table: "Users",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "LegacyEncryptedPrivateKey",
                table: "Users",
                type: "bytea",
                maxLength: 4096,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LegacyMemberKeyVersion",
                table: "Users",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MemberPublicKeySuiteId",
                table: "Users",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            // Every Member key registered before this migration is a frozen-suite X25519 key.
            migrationBuilder.Sql(
                "UPDATE \"Users\" SET \"MemberPublicKeySuiteId\" = 'palladin-x25519-sealed-box-v1' WHERE \"PublicKey\" IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastSecurityUpgradeId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "LegacyEncryptedPrivateKey",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "LegacyMemberKeyVersion",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "MemberPublicKeySuiteId",
                table: "Users");
        }
    }
}
