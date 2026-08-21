using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Palladin.Module.Vault.Domain;

namespace Palladin.Module.Vault.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class RoleVaultAccessOperationConfiguration : IEntityTypeConfiguration<RoleVaultAccessOperation>
{
    public void Configure(EntityTypeBuilder<RoleVaultAccessOperation> builder)
    {
        builder.HasKey(x => new { x.OrganizationId, x.Id });
        builder.HasAlternateKey(x => x.Id);
        builder.Property(x => x.UpdatedAt).IsConcurrencyToken();
        builder.HasIndex(x => new { x.OrganizationId, x.RoleId, x.CreatedAt });
    }
}
