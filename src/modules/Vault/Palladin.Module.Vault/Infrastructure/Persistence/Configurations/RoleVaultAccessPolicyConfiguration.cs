using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Palladin.Module.Vault.Domain;

namespace Palladin.Module.Vault.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class RoleVaultAccessPolicyConfiguration : IEntityTypeConfiguration<RoleVaultAccessPolicy>
{
    public void Configure(EntityTypeBuilder<RoleVaultAccessPolicy> builder)
    {
        builder.HasKey(x => new { x.OrganizationId, x.RoleId, x.VaultId });

        builder.HasOne(x => x.PolicySet)
            .WithMany(x => x.Policies)
            .HasForeignKey(x => new { x.OrganizationId, x.RoleId })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.Vault)
            .WithMany()
            .HasForeignKey(x => new { x.OrganizationId, Id = x.VaultId })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasQueryFilter(x => !x.Vault.IsDeleting);
    }
}
