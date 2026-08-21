using Palladin.Module.Identity.Domain;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Palladin.Module.Identity.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class OrganizationMemberRoleConfiguration : IEntityTypeConfiguration<OrganizationMemberRole>
{
    public void Configure(EntityTypeBuilder<OrganizationMemberRole> builder)
    {
        builder.HasKey(x => new { x.OrganizationId, x.UserId, x.RoleId });

        builder.HasOne(x => x.Member)
            .WithMany(x => x.RoleAssignments)
            .HasForeignKey(x => new { x.OrganizationId, x.UserId });

        builder.HasOne(x => x.Role)
            .WithMany(x => x.MemberAssignments)
            .HasForeignKey(x => new { x.OrganizationId, x.RoleId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
