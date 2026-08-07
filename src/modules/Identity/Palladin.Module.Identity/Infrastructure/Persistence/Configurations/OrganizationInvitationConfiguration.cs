using Palladin.Module.Identity.Domain;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Palladin.Module.Identity.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class OrganizationInvitationConfiguration : IEntityTypeConfiguration<OrganizationInvitation>
{
    public void Configure(EntityTypeBuilder<OrganizationInvitation> builder)
    {
        builder.HasKey(x => x.Id);
        builder.HasIndex(x => x.TokenHash).IsUnique();
        builder.HasIndex(x => new { x.OrganizationId, x.Email });

        builder.HasOne(x => x.Organization)
            .WithMany(x => x.Invitations)
            .HasForeignKey(x => x.OrganizationId);

        builder.HasOne(x => x.Role)
            .WithMany()
            .HasForeignKey(x => new { x.OrganizationId, x.RoleId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id });
    }
}
