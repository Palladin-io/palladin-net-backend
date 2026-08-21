using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Palladin.Module.Vault.Domain;

namespace Palladin.Module.Vault.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class RoleVaultAccessPolicySetConfiguration : IEntityTypeConfiguration<RoleVaultAccessPolicySet>
{
    public void Configure(EntityTypeBuilder<RoleVaultAccessPolicySet> builder)
    {
        builder.HasKey(x => new { x.OrganizationId, x.RoleId });
        builder.Property(x => x.Revision)
            .HasConversion(x => (decimal)x, x => (ulong)x)
            .HasPrecision(20, 0)
            .IsConcurrencyToken();
        builder.Property(x => x.AuthorizedRoleRevision)
            .HasConversion(x => (decimal)x, x => (ulong)x)
            .HasPrecision(20, 0);

        builder.HasOne(x => x.Role)
            .WithOne()
            .HasForeignKey<RoleVaultAccessPolicySet>(x => new { x.OrganizationId, x.RoleId })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
