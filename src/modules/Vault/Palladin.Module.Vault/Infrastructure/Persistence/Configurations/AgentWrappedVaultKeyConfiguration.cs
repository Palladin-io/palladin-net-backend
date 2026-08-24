using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Crypto;

namespace Palladin.Module.Vault.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class AgentWrappedVaultKeyConfiguration : IEntityTypeConfiguration<AgentWrappedVaultKey>
{
    public void Configure(EntityTypeBuilder<AgentWrappedVaultKey> builder)
    {
        builder.HasKey(x => new { x.OrganizationId, x.VaultId, x.GrantId });
        builder.Property(x => x.AgentAccessEpoch).IsRequired();
        builder.Property(x => x.ProtocolVersion).IsRequired();
        builder.Property(x => x.WrapperSuiteId).HasMaxLength(64).IsRequired();
        builder.Property(x => x.VaultKeyVersion)
            .HasConversion(x => (decimal)x.Value, x => new VaultKeyVersion((uint)x))
            .HasPrecision(10, 0);
        builder.Property(x => x.RecipientAgentKeyVersion)
            .HasConversion(x => (decimal)x.Value, x => new AgentRecipientKeyVersion((uint)x))
            .HasPrecision(10, 0);
        builder.Property(x => x.RecipientAgentKeyFingerprint)
            .HasMaxLength(VaultProtocol.FingerprintBytes)
            .IsRequired();
        builder.Property(x => x.EncodedSealedVaultKeyPackage)
            .HasMaxLength(X25519SealedBoxContract.EncodedPackageBytes)
            .IsRequired();
        builder.HasIndex(x => new { x.OrganizationId, x.VaultId, x.AgentId }).IsUnique();
        builder.ToTable("AgentWrappedVaultKeys");
    }
}
