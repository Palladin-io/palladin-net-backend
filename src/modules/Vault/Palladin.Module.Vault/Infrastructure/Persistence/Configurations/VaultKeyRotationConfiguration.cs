using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Palladin.Module.Vault.Domain;

namespace Palladin.Module.Vault.Infrastructure.Persistence.Configurations;

internal sealed class VaultKeyRotationConfiguration : IEntityTypeConfiguration<VaultKeyRotation>
{
    public void Configure(EntityTypeBuilder<VaultKeyRotation> builder)
    {
        builder.HasKey(x => new { x.OrganizationId, x.VaultId, x.Id });
        builder.HasIndex(x => new { x.OrganizationId, x.VaultId })
            .IsUnique()
            .HasFilter($"\"Status\" <> {(int)VaultKeyRotationStatus.Committed}");
        builder.HasOne<Domain.Vault>()
            .WithMany()
            .HasForeignKey(x => new { x.OrganizationId, x.VaultId })
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(x => x.Cause);
        builder.Property(x => x.Scope);
        builder.Property(x => x.Status);
        builder.Property(x => x.BaseMemberKeyGeneration)
            .HasConversion(x => (decimal)x.Value, x => new MemberKeyGeneration((uint)x))
            .HasPrecision(10, 0);
        builder.Property(x => x.TargetMemberKeyGeneration)
            .HasConversion(x => (decimal)x.Value, x => new MemberKeyGeneration((uint)x))
            .HasPrecision(10, 0);
        builder.Property(x => x.BaseMemberSequence)
            .HasConversion(x => (decimal)x.Value, x => new MemberSequence((ulong)x))
            .HasPrecision(20, 0);
        builder.Property(x => x.BaseDiscoverySequence)
            .HasConversion(x => (decimal)x.Value, x => new DiscoverySequence((ulong)x))
            .HasPrecision(20, 0);
        builder.Property(x => x.LeaseRevision).HasPrecision(20, 0).IsConcurrencyToken();
        builder.Property(x => x.TriggeredBy);
        builder.Property(x => x.TriggeredAt);
        builder.Property(x => x.LeaseOwnerId);
        builder.Property(x => x.FencingToken);
        builder.Property(x => x.LeaseExpiresAt);
        builder.Property(x => x.CommittedAt);
        builder.Property(x => x.LastFailureCode).HasMaxLength(64);
        builder.Property(x => x.DeprovisioningId);
        builder.Property(x => x.ExcludedMemberId);
        builder.Property(x => x.ExcludedAgentId);
        builder.HasOne<VaultPrincipalDeprovisioning>()
            .WithMany()
            .HasForeignKey(x => new { x.OrganizationId, x.DeprovisioningId })
            .OnDelete(DeleteBehavior.Restrict)
            .IsRequired(false);

        builder.OwnsOne(x => x.BaseKeyEpoch, epoch => ConfigureEpoch(epoch));
        builder.OwnsOne(x => x.TargetKeyEpoch, epoch => ConfigureEpoch(epoch));
    }

    private static void ConfigureEpoch<TOwner>(OwnedNavigationBuilder<TOwner, VaultKeyEpoch> epoch)
        where TOwner : class
    {
        epoch.Property(x => x.VaultKeyVersion)
            .HasConversion(x => (decimal)x.Value, x => new VaultKeyVersion((uint)x))
            .HasPrecision(10, 0);
        epoch.Property(x => x.VdkVersion)
            .HasConversion(x => (decimal)x.Value, x => new VdkVersion((uint)x))
            .HasPrecision(10, 0);
        epoch.Property(x => x.AgentMessageKeyVersion)
            .HasConversion(x => (decimal)x.Value, x => new AgentMessageKeyVersion((uint)x))
            .HasPrecision(10, 0);
        epoch.Property(x => x.ManifestSigningKeyVersion)
            .HasConversion(x => (decimal)x.Value, x => new ManifestSigningKeyVersion((uint)x))
            .HasPrecision(10, 0);
    }
}
