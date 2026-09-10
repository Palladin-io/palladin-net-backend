using Palladin.Module.Identity.Domain;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Palladin.Module.Identity.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class OrganizationMemberConfiguration : IEntityTypeConfiguration<OrganizationMember>
{
    public void Configure(EntityTypeBuilder<OrganizationMember> builder)
    {
        builder.HasKey(x => new { x.OrganizationId, x.UserId });
        builder.Property(x => x.Status).HasDefaultValue(OrganizationMemberStatus.Active).IsConcurrencyToken();
        builder.Property(x => x.AuthorizationVersion)
            .HasDefaultValue(1u)
            .IsConcurrencyToken();

        builder.HasOne(x => x.Organization)
            .WithMany(x => x.Members)
            .HasForeignKey(x => x.OrganizationId);

        builder.HasOne(x => x.User)
            .WithMany(x => x.OrganizationMemberships)
            .HasForeignKey(x => x.UserId);

    }
}
