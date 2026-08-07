using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Palladin.Module.Vault.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class VaultEntryVersionConfiguration : IEntityTypeConfiguration<Domain.VaultEntryVersion>
{
    public void Configure(EntityTypeBuilder<Domain.VaultEntryVersion> builder)
    {
        builder.HasKey(x => new { x.OrganizationId, x.VaultId, x.EntryId, x.Revision });
        builder.Property(x => x.Revision)
            .HasConversion(x => (decimal)x.Value, x => new Domain.EntryRevision((ulong)x))
            .HasPrecision(20, 0);
        builder.Property(x => x.MemberSequence)
            .HasConversion(x => (decimal)x.Value, x => new Domain.MemberSequence((ulong)x))
            .HasPrecision(20, 0);
        builder.Property(x => x.DiscoverySequence)
            .HasConversion(
                x => x.HasValue ? (decimal?)x.Value.Value : null,
                x => x.HasValue ? new Domain.DiscoverySequence((ulong)x.Value) : null)
            .HasPrecision(20, 0);
        builder.Property(x => x.MemberIndexChanged).IsRequired();
        builder.Property(x => x.KeyVersion)
            .HasConversion(x => (decimal)x.Value, x => new Domain.EntryKeyVersion((uint)x))
            .HasPrecision(10, 0);
        builder.Property(x => x.MemberKeyGeneration)
            .HasConversion(x => (decimal)x.Value, x => new Domain.MemberKeyGeneration((uint)x))
            .HasPrecision(10, 0);
        builder.Property(x => x.ChangedAt).IsRequired();
        builder.Property(x => x.ChangedByType).IsRequired();
        builder.Property(x => x.ChangedById).IsRequired();
        builder.Property(x => x.Operation).IsRequired();
        builder.Property(x => x.ProtocolVersion).IsRequired();
        builder.Property(x => x.CryptoSuiteId).HasMaxLength(64).IsRequired();
        builder.Property(x => x.MemberSecretEncodedSuitePayload).HasMaxLength(262_168).IsRequired();

        builder.HasIndex(x => new { x.OrganizationId, x.VaultId, x.MemberSequence }).IsUnique();
        builder.HasIndex(x => new { x.OrganizationId, x.VaultId, x.DiscoverySequence })
            .IsUnique()
            .HasFilter("\"DiscoverySequence\" IS NOT NULL");

        builder.HasOne<Domain.VaultEntryKey>()
            .WithMany()
            .HasForeignKey(x => new { x.OrganizationId, x.VaultId, x.EntryId, x.KeyVersion })
            .OnDelete(DeleteBehavior.Restrict);

        builder.ToTable("VaultEntryVersions");
    }
}
