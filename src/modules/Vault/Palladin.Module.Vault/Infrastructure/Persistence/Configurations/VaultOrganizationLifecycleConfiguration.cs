using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Palladin.Module.Vault.Domain;

namespace Palladin.Module.Vault.Infrastructure.Persistence.Configurations;

internal sealed class VaultOrganizationLifecycleConfiguration
    : IEntityTypeConfiguration<VaultOrganizationLifecycle>
{
    public void Configure(EntityTypeBuilder<VaultOrganizationLifecycle> builder)
    {
        builder.HasKey(x => x.OrganizationId);
        builder.Property(x => x.MutationVersion)
            .HasConversion(x => (decimal)x, x => (ulong)x)
            .HasPrecision(20, 0)
            .IsConcurrencyToken();
    }
}
