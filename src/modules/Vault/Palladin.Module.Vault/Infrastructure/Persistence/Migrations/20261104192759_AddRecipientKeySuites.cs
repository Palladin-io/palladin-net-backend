using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Palladin.Module.Vault.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRecipientKeySuites : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AgentMessageKeySuiteId",
                table: "Vaults",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "palladin-x25519-sealed-box-v1");

            migrationBuilder.AddColumn<string>(
                name: "WrapperSuiteId",
                table: "ScriptExecutionPackages",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "palladin-x25519-sealed-box-v1");

            migrationBuilder.AddColumn<string>(
                name: "MessageKeySuiteId",
                table: "OrganizationApproverKeySets",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "palladin-x25519-sealed-box-v1");

            migrationBuilder.AlterColumn<string>(
                name: "AgentPublicKey",
                table: "Grants",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(512)",
                oldMaxLength: 512);

            migrationBuilder.AddColumn<string>(
                name: "WrapperSuiteId",
                table: "ApproverKeyWraps",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "palladin-x25519-sealed-box-v1");

            migrationBuilder.AddColumn<string>(
                name: "VaultAgentMessageKeySuiteId",
                table: "AgentVaultDiscoveryEnvelope",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "PublicKey",
                table: "Agents",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(512)",
                oldMaxLength: 512);

            migrationBuilder.AddColumn<string>(
                name: "PublicKeySuiteId",
                table: "Agents",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "palladin-x25519-sealed-box-v1");

            // Every existing row was produced under the frozen X25519 suite; new rows always carry their suite.
            migrationBuilder.Sql("ALTER TABLE \"Vaults\" ALTER COLUMN \"AgentMessageKeySuiteId\" DROP DEFAULT;");
            migrationBuilder.Sql("ALTER TABLE \"ScriptExecutionPackages\" ALTER COLUMN \"WrapperSuiteId\" DROP DEFAULT;");
            migrationBuilder.Sql("ALTER TABLE \"OrganizationApproverKeySets\" ALTER COLUMN \"MessageKeySuiteId\" DROP DEFAULT;");
            migrationBuilder.Sql("ALTER TABLE \"ApproverKeyWraps\" ALTER COLUMN \"WrapperSuiteId\" DROP DEFAULT;");
            migrationBuilder.Sql("ALTER TABLE \"Agents\" ALTER COLUMN \"PublicKeySuiteId\" DROP DEFAULT;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AgentMessageKeySuiteId",
                table: "Vaults");

            migrationBuilder.DropColumn(
                name: "WrapperSuiteId",
                table: "ScriptExecutionPackages");

            migrationBuilder.DropColumn(
                name: "MessageKeySuiteId",
                table: "OrganizationApproverKeySets");

            migrationBuilder.DropColumn(
                name: "WrapperSuiteId",
                table: "ApproverKeyWraps");

            migrationBuilder.DropColumn(
                name: "VaultAgentMessageKeySuiteId",
                table: "AgentVaultDiscoveryEnvelope");

            migrationBuilder.DropColumn(
                name: "PublicKeySuiteId",
                table: "Agents");

            migrationBuilder.AlterColumn<string>(
                name: "AgentPublicKey",
                table: "Grants",
                type: "character varying(512)",
                maxLength: 512,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(2048)",
                oldMaxLength: 2048);

            migrationBuilder.AlterColumn<string>(
                name: "PublicKey",
                table: "Agents",
                type: "character varying(512)",
                maxLength: 512,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(2048)",
                oldMaxLength: 2048);
        }
    }
}
