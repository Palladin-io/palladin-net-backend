using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Palladin.Module.Identity.Domain;

namespace Palladin.Module.Identity.Infrastructure.Persistence.Configurations;

internal sealed class OrganizationMemberDirectoryEntryConfiguration
    : IEntityTypeConfiguration<OrganizationMemberDirectoryEntry>
{
    public void Configure(EntityTypeBuilder<OrganizationMemberDirectoryEntry> builder)
    {
        builder.HasKey(x => new { x.OrganizationId, x.UserId });
        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(x => x.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
