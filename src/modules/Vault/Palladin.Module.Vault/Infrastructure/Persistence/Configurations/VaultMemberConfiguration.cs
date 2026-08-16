using Palladin.Module.Vault.Domain;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Palladin.Module.Vault.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class VaultMemberConfiguration : IEntityTypeConfiguration<VaultMember>
{
    public void Configure(EntityTypeBuilder<VaultMember> builder)
    {
        builder.HasKey(x => new { x.OrganizationId, x.VaultId, x.UserId });
        builder.HasQueryFilter(x => !x.Vault.IsDeleting);

        builder.HasOne(x => x.Vault)
            .WithMany(x => x.VaultMembers)
            .HasForeignKey(x => new { x.OrganizationId, x.VaultId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
