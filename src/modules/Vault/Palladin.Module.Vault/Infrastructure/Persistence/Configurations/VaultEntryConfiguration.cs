using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Palladin.Module.Vault.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class VaultEntryConfiguration : IEntityTypeConfiguration<Domain.VaultEntry>
{
    public void Configure(EntityTypeBuilder<Domain.VaultEntry> builder)
    {
        builder.HasKey(x => new { x.OrganizationId, x.VaultId, x.Id });

        builder.Property(x => x.CurrentRevision)
            .HasConversion(x => (decimal)x.Value, x => new Domain.EntryRevision((ulong)x))
            .HasPrecision(20, 0);
        builder.Property(x => x.MemberIndexRevision)
            .HasConversion(x => (decimal)x.Value, x => new Domain.MemberIndexRevision((ulong)x))
            .HasPrecision(20, 0);
        builder.Property(x => x.AgentDiscoveryRevision)
            .HasConversion(
                x => x.HasValue ? (decimal?)x.Value.Value : null,
                x => x.HasValue ? new Domain.AgentDiscoveryRevision((ulong)x.Value) : null)
            .HasPrecision(20, 0);
        builder.Property(x => x.AgentDiscoveryRevisionHighWatermark)
            .HasConversion(
                x => (decimal)x.Value,
                x => new Domain.AgentDiscoveryRevisionWatermark((ulong)x))
            .HasPrecision(20, 0);
        builder.Property(x => x.CurrentKeyVersion)
            .HasConversion(x => (decimal)x.Value, x => new Domain.EntryKeyVersion((uint)x))
            .HasPrecision(10, 0);
        builder.Property(x => x.MemberIndexMemberKeyGeneration)
            .HasConversion(x => (decimal)x.Value, x => new Domain.MemberKeyGeneration((uint)x))
            .HasPrecision(10, 0);
        builder.Property(x => x.MemberIndexProtocolVersion).IsRequired();
        builder.Property(x => x.MemberIndexCryptoSuiteId).HasMaxLength(64).IsRequired();
        builder.Property(x => x.AgentDiscoveryVdkVersion)
            .HasConversion(
                x => x.HasValue ? (decimal?)x.Value.Value : null,
                x => x.HasValue ? new Domain.VdkVersion((uint)x.Value) : null)
            .HasPrecision(10, 0);
        builder.Property(x => x.AgentDiscoveryMemberKeyGeneration)
            .HasConversion(
                x => x.HasValue ? (decimal?)x.Value.Value : null,
                x => x.HasValue ? new Domain.MemberKeyGeneration((uint)x.Value) : null)
            .HasPrecision(10, 0);
        builder.Property(x => x.AgentDiscoveryProtocolVersion);
        builder.Property(x => x.AgentDiscoveryCryptoSuiteId).HasMaxLength(64);

        builder.Property(x => x.MemberIndexEncodedSuitePayload).HasMaxLength(32_792).IsRequired();
        builder.Property(x => x.AgentDiscoveryEncodedSuitePayload).HasMaxLength(16_408);
        builder.Property(x => x.IsPurging).IsRequired();
        builder.Property(x => x.PurgeLedgerRequired).IsRequired();
        builder.Property(x => x.PurgeRequestedAt);
        builder.Property(x => x.PurgeRequestedBy);
        builder.Property(x => x.UpdatedAt).IsConcurrencyToken();

        // Purge is a durable, fail-closed state while the idempotent ledger and object-storage
        // phases run outside the database transaction.
        builder.HasQueryFilter(x => !x.IsPurging);

        builder.HasIndex(x => new { x.OrganizationId, x.VaultId, x.CreatedAt, x.Id });

        builder.HasOne<Domain.Vault>()
            .WithMany()
            .HasForeignKey(x => new { x.OrganizationId, x.VaultId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(x => x.Keys)
            .WithOne()
            .HasForeignKey(x => new { x.OrganizationId, x.VaultId, x.EntryId })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(x => x.Versions)
            .WithOne()
            .HasForeignKey(x => new { x.OrganizationId, x.VaultId, x.EntryId })
            .OnDelete(DeleteBehavior.Cascade);

        builder.ToTable("VaultEntries");
    }
}
