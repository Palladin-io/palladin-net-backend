using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Palladin.Module.Vault.Domain;

namespace Palladin.Module.Vault.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class OrganizationMemberRoleSetConfiguration : IEntityTypeConfiguration<OrganizationMemberRoleSet>
{
    public void Configure(EntityTypeBuilder<OrganizationMemberRoleSet> builder)
    {
        builder.HasKey(x => new { x.OrganizationId, x.UserId });
        builder.Property(x => x.RoleIds).HasColumnType("uuid[]");
        builder.Property(x => x.Revision)
            .HasConversion(x => (decimal)x, x => (ulong)x)
            .HasPrecision(20, 0)
            .IsConcurrencyToken();
        builder.Property(x => x.AuthorizationVersion).HasPrecision(10, 0);
        builder.Property(x => x.UpdatedAt).IsConcurrencyToken();
        builder.HasIndex(x => new { x.OrganizationId, x.IsActive });
    }
}
