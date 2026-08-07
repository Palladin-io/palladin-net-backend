using Palladin.Module.Search.Domain;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Palladin.Module.Search.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class SearchItemConfiguration : IEntityTypeConfiguration<SearchItem>
{
    public void Configure(EntityTypeBuilder<SearchItem> builder)
    {
        builder.HasKey(x => new { x.OrganizationId, x.Id });

        builder.Property(x => x.Type).HasMaxLength(32);
        builder.Property(x => x.Name).HasMaxLength(256);
        builder.Property(x => x.SearchText).HasMaxLength(1024);
        builder.Property(x => x.UpdatedAt).IsConcurrencyToken();

        builder.HasIndex(x => new { x.OrganizationId, x.Type, x.Name });
    }
}
