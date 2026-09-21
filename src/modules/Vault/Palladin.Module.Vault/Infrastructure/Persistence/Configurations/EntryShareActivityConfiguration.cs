using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Palladin.Module.Vault.Domain;

namespace Palladin.Module.Vault.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class EntryShareActivityConfiguration : IEntityTypeConfiguration<EntryShareActivity>
{
    public void Configure(EntityTypeBuilder<EntryShareActivity> builder)
    {
        builder.ToTable("EntryShareActivities");
        builder.HasKey(x => new { x.ShareId, x.Sequence });
        builder.HasIndex(x => new { x.OccurredAt, x.ShareId, x.Sequence })
            .HasFilter("\"PublishedAt\" IS NULL");
        builder.HasIndex(x => new { x.PublishedAt, x.ShareId, x.Sequence })
            .HasFilter("\"PublishedAt\" IS NOT NULL");
    }
}
