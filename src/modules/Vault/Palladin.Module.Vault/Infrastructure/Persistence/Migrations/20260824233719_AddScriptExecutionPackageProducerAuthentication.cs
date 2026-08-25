using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Palladin.Module.Vault.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddScriptExecutionPackageProducerAuthentication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Unsigned packages cannot be authenticated or safely backfilled. Invalidate the
            // pre-release grants atomically; their package/scope rows cascade with the Grant.
            migrationBuilder.Sql("""
                DELETE FROM "Grants"
                WHERE "GrantType" = 'ScriptExecution';
                """);

            migrationBuilder.AddColumn<byte[]>(
                name: "ProducerSignature",
                table: "ScriptExecutionPackages",
                type: "bytea",
                maxLength: 64,
                nullable: false);

            migrationBuilder.AddColumn<byte[]>(
                name: "VaultSigningKeyFingerprint",
                table: "ScriptExecutionPackages",
                type: "bytea",
                maxLength: 32,
                nullable: false);

            migrationBuilder.AddColumn<long>(
                name: "VaultSigningKeyVersion",
                table: "ScriptExecutionPackages",
                type: "bigint",
                nullable: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProducerSignature",
                table: "ScriptExecutionPackages");

            migrationBuilder.DropColumn(
                name: "VaultSigningKeyFingerprint",
                table: "ScriptExecutionPackages");

            migrationBuilder.DropColumn(
                name: "VaultSigningKeyVersion",
                table: "ScriptExecutionPackages");
        }
    }
}
