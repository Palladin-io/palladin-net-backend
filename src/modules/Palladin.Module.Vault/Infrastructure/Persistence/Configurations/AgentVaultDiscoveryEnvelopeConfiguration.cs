using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Crypto;

namespace Palladin.Module.Vault.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class AgentVaultDiscoveryEnvelopeConfiguration
    : IEntityTypeConfiguration<AgentVaultDiscoveryEnvelope>
{
    public void Configure(EntityTypeBuilder<AgentVaultDiscoveryEnvelope> builder)
    {
        builder.HasKey(x => new { x.OrganizationId, x.VaultId, x.AgentId });
        builder.Property(x => x.ProtocolVersion).IsRequired();
        builder.Property(x => x.CryptoSuiteId).HasMaxLength(64).IsRequired();
        builder.Property(x => x.WrapperSuiteId).HasMaxLength(64).IsRequired();
        builder.Property(x => x.RecipientAgentKeyFingerprint).HasMaxLength(32).IsRequired();
        builder.Property(x => x.AgentWrappedVdk).HasMaxLength(X25519SealedBoxContract.EncodedPackageBytes).IsRequired();
        builder.Property(x => x.ManifestSignature).HasMaxLength(64).IsRequired();
        builder.Property(x => x.AgentX25519Fingerprint).HasMaxLength(32).IsRequired();
        builder.Property(x => x.AgentEd25519Fingerprint).HasMaxLength(32).IsRequired();
        builder.Property(x => x.VaultSigningPublicKey).HasMaxLength(32).IsRequired();
        builder.Property(x => x.VaultSigningKeyFingerprint).HasMaxLength(32).IsRequired();
        builder.Property(x => x.VaultAgentMessagePublicKey).HasMaxLength(32).IsRequired();
        builder.Property(x => x.VaultAgentMessageKeyFingerprint).HasMaxLength(32).IsRequired();
        builder.Property(x => x.AgentWrappedVdkDigest).HasMaxLength(32).IsRequired();
        builder.Property(x => x.IssuedAt).IsRequired();
        builder.Property(x => x.MinimumAgentRuntimeProtocol).IsRequired();
        builder.Property(x => x.ProvisionedBy).IsRequired();
        builder.Property(x => x.ProvisionedAt).IsRequired();
        builder.Property(x => x.ProvisionedAccessEpoch).IsRequired();
        builder.Property(x => x.RevokedAt);
        builder.Property(x => x.VdkVersion)
            .HasConversion(x => (decimal)x.Value, x => new VdkVersion((uint)x))
            .HasPrecision(10, 0);
        builder.Property(x => x.RecipientAgentKeyVersion)
            .HasConversion(x => (decimal)x.Value, x => new AgentRecipientKeyVersion((uint)x))
            .HasPrecision(10, 0);
        builder.Property(x => x.ManifestRevision)
            .HasConversion(x => (decimal)x.Value, x => new ManifestRevision((ulong)x))
            .HasPrecision(20, 0);
        builder.Property(x => x.ManifestSigningKeyVersion)
            .HasConversion(x => (decimal)x.Value, x => new ManifestSigningKeyVersion((uint)x))
            .HasPrecision(10, 0);
        builder.Property(x => x.AgentMessageKeyVersion)
            .HasConversion(x => (decimal)x.Value, x => new AgentMessageKeyVersion((uint)x))
            .HasPrecision(10, 0);

        builder.HasOne<Domain.Vault>()
            .WithMany(x => x.AgentVaultDiscoveryEnvelopes)
            .HasForeignKey(x => new { x.OrganizationId, x.VaultId })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Agent>()
            .WithMany()
            .HasForeignKey(x => new { x.OrganizationId, x.AgentId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.ToTable("AgentVaultDiscoveryEnvelope");
    }
}
