using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Palladin.Module.Vault.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMemberKeyDirectorySuite : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PublicKeySuiteId",
                table: "MemberKeyDirectory",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "palladin-x25519-sealed-box-v1");

            // Existing directory keys are all frozen-suite X25519 keys; new rows always carry Identity's suite.
            migrationBuilder.Sql(
                "ALTER TABLE \"MemberKeyDirectory\" ALTER COLUMN \"PublicKeySuiteId\" DROP DEFAULT;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PublicKeySuiteId",
                table: "MemberKeyDirectory");
        }
    }
}
