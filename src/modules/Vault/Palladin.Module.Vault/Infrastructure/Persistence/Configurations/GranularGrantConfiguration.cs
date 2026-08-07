using Palladin.Module.Vault.Domain;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Palladin.Module.Vault.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class GranularGrantConfiguration : IEntityTypeConfiguration<GranularGrant>
{
    public void Configure(EntityTypeBuilder<GranularGrant> builder)
    {
        // Entry filter for the org grants panel: GRANULAR-only ("just this entry"). EntryId is null for
        // FULL grants (TPH single table), so a partial index keeps it small and matches the query shape.
        // Includes the newest-first sort keys so the planner can filter and order from one index.
        builder.HasIndex(x => new { x.EntryId, x.OrganizationId, x.CreatedAt, x.Id })
            .HasFilter("\"EntryId\" IS NOT NULL");
    }
}
