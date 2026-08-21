using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Palladin.Module.Vault.Domain;

namespace Palladin.Module.Vault.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class OrganizationRoleDirectoryEntryConfiguration
    : IEntityTypeConfiguration<OrganizationRoleDirectoryEntry>
{
    public void Configure(EntityTypeBuilder<OrganizationRoleDirectoryEntry> builder)
    {
        builder.HasKey(x => new { x.OrganizationId, x.RoleId });
        builder.Property(x => x.SourceRevision)
            .HasConversion(x => (decimal)x, x => (ulong)x)
            .HasPrecision(20, 0);
        builder.Property(x => x.MutationVersion)
            .HasConversion(x => (decimal)x, x => (ulong)x)
            .HasPrecision(20, 0)
            .IsConcurrencyToken();
    }
}
