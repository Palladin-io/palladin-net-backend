using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Palladin.Module.PublicAssetCatalog.Domain;

namespace Palladin.Module.PublicAssetCatalog.Infrastructure.Persistence;

internal sealed class PublicAssetConfiguration : IEntityTypeConfiguration<PublicAsset>
{
    public void Configure(EntityTypeBuilder<PublicAsset> b)
    {
        b.HasKey(x => x.Id); b.Property(x => x.Name).HasMaxLength(200);
        b.HasMany(x => x.Aliases).WithOne().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Revisions).WithOne().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.Type, x.Status, x.Name });
        b.HasIndex(x => new { x.OrganizationId, x.Type, x.OwnerId }).IsUnique();
    }
}
internal sealed class PublicAssetAliasConfiguration : IEntityTypeConfiguration<PublicAssetAlias>
{
    public void Configure(EntityTypeBuilder<PublicAssetAlias> b) { b.HasKey(x => new { x.AssetId, x.Kind, x.Value }); b.Property(x => x.Value).HasMaxLength(253); b.HasIndex(x => new { x.Kind, x.Value }).IsUnique().HasFilter("\"Kind\" = 1"); }
}
internal sealed class PublicAssetRevisionConfiguration : IEntityTypeConfiguration<PublicAssetRevision>
{
    public void Configure(EntityTypeBuilder<PublicAssetRevision> b) { b.HasKey(x => new { x.AssetId, x.Revision }); b.Property(x => x.Digest).HasMaxLength(64); b.Property(x => x.MediaType).HasMaxLength(32); b.Property(x => x.StorageKey).HasMaxLength(512); b.HasIndex(x => x.Digest); }
}
internal sealed class PublicAssetUploadSessionConfiguration : IEntityTypeConfiguration<PublicAssetUploadSession>
{
    public void Configure(EntityTypeBuilder<PublicAssetUploadSession> b) { b.HasKey(x => x.Id); b.Property(x => x.ServiceSubject).HasMaxLength(64); b.Property(x => x.ExpectedDigest).HasMaxLength(64); b.Property(x => x.ExpectedMediaType).HasMaxLength(32); b.Property(x => x.StagingKey).HasMaxLength(512); b.HasIndex(x => new { x.UploaderId, x.ExpiresAt }); }
}
