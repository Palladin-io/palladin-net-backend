using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Palladin.Module.Vault.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class VaultConfiguration : IEntityTypeConfiguration<Domain.Vault>
{
    public void Configure(EntityTypeBuilder<Domain.Vault> builder)
    {
        builder.HasKey(x => new { x.OrganizationId, x.Id });
        builder.HasAlternateKey(x => x.Id);

        builder.Property(x => x.ProtocolVersion).IsRequired();
        builder.Property(x => x.MemberVaultMetadataCryptoSuiteId).HasMaxLength(64).IsRequired();
        builder.Property(x => x.MemberVaultMetadataEncodedSuitePayload)
            .HasMaxLength(Domain.VaultProtocol.NonceBytes + Domain.VaultProtocol.MaximumMetadataCiphertextBytes)
            .IsRequired();
        builder.Property(x => x.ManifestSigningPublicKey).HasMaxLength(32).IsRequired();
        builder.Property(x => x.ManifestSigningKeyFingerprint).HasMaxLength(32).IsRequired();
        builder.Property(x => x.AgentMessagePublicKey).HasMaxLength(32).IsRequired();
        builder.Property(x => x.AgentMessageKeyFingerprint).HasMaxLength(32).IsRequired();
        builder.Property(x => x.MetadataRevision)
            .HasConversion(x => (decimal)x.Value, x => new Domain.MetadataRevision((ulong)x))
            .HasPrecision(20, 0);
        builder.Property(x => x.MemberSequence)
            .HasConversion(x => (decimal)x.Value, x => new Domain.MemberSequence((ulong)x))
            .HasPrecision(20, 0);
        builder.Property(x => x.DiscoverySequence)
            .HasConversion(x => (decimal)x.Value, x => new Domain.DiscoverySequence((ulong)x))
            .HasPrecision(20, 0);
        builder.Property(x => x.MinRetainedMemberSequence)
            .HasConversion(x => (decimal)x.Value, x => new Domain.MemberSequence((ulong)x))
            .HasPrecision(20, 0);
        builder.Property(x => x.MinRetainedDiscoverySequence)
            .HasConversion(x => (decimal)x.Value, x => new Domain.DiscoverySequence((ulong)x))
            .HasPrecision(20, 0);
        builder.Property(x => x.MemberKeyGeneration)
            .HasConversion(x => (decimal)x.Value, x => new Domain.MemberKeyGeneration((uint)x))
            .HasPrecision(10, 0);
        builder.Property(x => x.CurrentVaultKeyVersion)
            .HasConversion(x => (decimal)x.Value, x => new Domain.VaultKeyVersion((uint)x))
            .HasPrecision(10, 0);
        builder.Property(x => x.CurrentVdkVersion)
            .HasConversion(x => (decimal)x.Value, x => new Domain.VdkVersion((uint)x))
            .HasPrecision(10, 0);
        builder.Property(x => x.CurrentAgentMessageKeyVersion)
            .HasConversion(x => (decimal)x.Value, x => new Domain.AgentMessageKeyVersion((uint)x))
            .HasPrecision(10, 0);
        builder.Property(x => x.CurrentManifestSigningKeyVersion)
            .HasConversion(x => (decimal)x.Value, x => new Domain.ManifestSigningKeyVersion((uint)x))
            .HasPrecision(10, 0);
        builder.Property(x => x.MemberVaultMetadataKeyVersion)
            .HasConversion(x => (decimal)x.Value, x => new Domain.VaultKeyVersion((uint)x))
            .HasPrecision(10, 0);
        builder.Property(x => x.MutationVersion)
            .HasConversion(x => (decimal)x, x => (ulong)x)
            .HasPrecision(20, 0)
            .IsConcurrencyToken();
        builder.Property(x => x.IsDeleting).IsRequired();
        builder.Property(x => x.DeletionRequestedBy);
        builder.Property(x => x.DeletionRequestedByName).HasMaxLength(200);
        builder.Property(x => x.DeletionRequestedAt);

        builder.Property(x => x.UpdatedAt).IsConcurrencyToken();

        // A requested deletion is fail-closed: normal reads and mutations cannot revive the
        // aggregate while external object cleanup is retried outside the database transaction.
        builder.HasQueryFilter(x => !x.IsDeleting);

        builder.HasIndex(x => x.CreatedBy)
            .IsUnique()
            .HasFilter("\"IsDefault\"");
    }
}
