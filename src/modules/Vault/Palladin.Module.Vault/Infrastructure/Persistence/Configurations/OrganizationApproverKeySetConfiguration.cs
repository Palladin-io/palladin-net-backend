using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Palladin.Module.Vault.Domain;

namespace Palladin.Module.Vault.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class OrganizationApproverKeySetConfiguration : IEntityTypeConfiguration<OrganizationApproverKeySet>
{
    public void Configure(EntityTypeBuilder<OrganizationApproverKeySet> builder)
    {
        builder.HasKey(x => x.OrganizationId);
        builder.Property(x => x.ManifestRevision).HasConversion(x => (decimal)x, x => (ulong)x).HasPrecision(20, 0);
        builder.Property(x => x.ApproverSetRevision).HasConversion(x => (decimal)x, x => (ulong)x).HasPrecision(20, 0);
        builder.Property(x => x.MessagePublicKey).HasMaxLength(32).IsRequired();
        builder.Property(x => x.MessageKeyFingerprint).HasMaxLength(32).IsRequired();
        builder.Property(x => x.SigningPublicKey).HasMaxLength(32).IsRequired();
        builder.Property(x => x.SigningKeyFingerprint).HasMaxLength(32).IsRequired();
        builder.Property(x => x.PreviousSigningKeyFingerprint).HasMaxLength(32);
        builder.Property(x => x.Version)
            .HasConversion(x => (decimal)x, x => (ulong)x)
            .HasPrecision(20, 0)
            .IsConcurrencyToken();
        builder.HasMany(x => x.Wraps)
            .WithOne()
            .HasForeignKey(x => x.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(x => x.Manifests)
            .WithOne()
            .HasForeignKey(x => x.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
