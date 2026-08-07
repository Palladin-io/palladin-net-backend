using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Palladin.Module.Vault.Domain;

namespace Palladin.Module.Vault.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class VaultPresentationAssetCutoverStateConfiguration
    : IEntityTypeConfiguration<VaultPresentationAssetCutoverState>
{
    public void Configure(EntityTypeBuilder<VaultPresentationAssetCutoverState> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasMaxLength(64);
        builder.HasData(new { Id = VaultPresentationAssetCutoverState.ZeroKnowledgeAssets });
        builder.ToTable("VaultPresentationAssetCutoverStates");
    }
}
