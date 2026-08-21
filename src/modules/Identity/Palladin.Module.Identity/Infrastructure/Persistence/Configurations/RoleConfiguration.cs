using Palladin.Module.Identity.Domain;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Palladin.Module.Identity.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.HasKey(x => new { x.OrganizationId, x.Id });
        builder.Property(x => x.Name).HasMaxLength(100);
        builder.Property(x => x.NormalizedName).HasMaxLength(100);
        builder.HasIndex(x => new { x.OrganizationId, x.NormalizedName }).IsUnique();

        builder.HasOne(x => x.Organization)
            .WithMany(x => x.Roles)
            .HasForeignKey(x => x.OrganizationId);
    }
}
