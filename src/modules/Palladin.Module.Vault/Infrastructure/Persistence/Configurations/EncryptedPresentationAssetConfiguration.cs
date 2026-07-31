using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Palladin.Module.Vault.Domain;

namespace Palladin.Module.Vault.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class EncryptedPresentationAssetConfiguration : IEntityTypeConfiguration<EncryptedPresentationAsset>
{
    public void Configure(EntityTypeBuilder<EncryptedPresentationAsset> builder)
    {
        builder.HasKey(x => new { x.OrganizationId, x.VaultId, x.Id });
        builder.HasIndex(x => x.StorageId).IsUnique();
        builder.HasIndex(x => new { x.OrganizationId, x.VaultId, x.EntryId });

        builder.Property(x => x.Target).HasConversion<short>();
        builder.Property(x => x.Status).HasConversion<short>();
        builder.Property(x => x.MediaType).HasMaxLength(32).IsRequired();
        builder.Property(x => x.CiphertextSha256).HasMaxLength(EncryptedPresentationAsset.DigestLength).IsRequired();

        builder.HasOne<Domain.Vault>()
            .WithMany()
            .HasForeignKey(x => new { x.OrganizationId, x.VaultId })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<VaultEntry>()
            .WithMany()
            .HasForeignKey(x => new { x.OrganizationId, x.VaultId, Id = x.EntryId })
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Cascade);

        builder.ToTable("EncryptedPresentationAssets");
    }
}
