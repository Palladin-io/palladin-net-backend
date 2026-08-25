using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Palladin.Module.Vault.Infrastructure.Persistence;

#nullable disable

namespace Palladin.Module.Vault.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
[DbContext(typeof(VaultDbWriteContext))]
[Migration("20260825050000_AddAgentWrappedVaultKeyProducerAuthentication")]
public partial class AddAgentWrappedVaultKeyProducerAuthentication : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Pre-release wrappers were not signed and cannot be authenticated or
        // truthfully backfilled without the client-only Vault signing key.
        migrationBuilder.Sql("""
            DELETE FROM "Grants"
            WHERE "GrantType" = 'Full';
            """);

        migrationBuilder.AddColumn<byte[]>(
            name: "ProducerSignature",
            table: "AgentWrappedVaultKeys",
            type: "bytea",
            maxLength: 64,
            nullable: false);

        migrationBuilder.AddColumn<byte[]>(
            name: "VaultSigningKeyFingerprint",
            table: "AgentWrappedVaultKeys",
            type: "bytea",
            maxLength: 32,
            nullable: false);

        migrationBuilder.AddColumn<decimal>(
            name: "VaultSigningKeyVersion",
            table: "AgentWrappedVaultKeys",
            type: "numeric(10,0)",
            precision: 10,
            nullable: false);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ProducerSignature",
            table: "AgentWrappedVaultKeys");

        migrationBuilder.DropColumn(
            name: "VaultSigningKeyFingerprint",
            table: "AgentWrappedVaultKeys");

        migrationBuilder.DropColumn(
            name: "VaultSigningKeyVersion",
            table: "AgentWrappedVaultKeys");
    }
}
