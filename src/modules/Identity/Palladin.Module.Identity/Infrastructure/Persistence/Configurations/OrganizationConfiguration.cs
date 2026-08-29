using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Contracts.ValueObjects;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Palladin.Module.Identity.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
{
    public void Configure(EntityTypeBuilder<Organization> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.MembershipVersion)
            .HasConversion(x => (decimal)x, x => (ulong)x)
            .HasPrecision(20, 0)
            .IsConcurrencyToken();
        builder.Property(x => x.OfflineAccessPolicy)
            .HasConversion<ushort>()
            .HasDefaultValue(OrganizationOfflineAccessPolicy.TwentyFourHours)
            .HasSentinel((OrganizationOfflineAccessPolicy)ushort.MaxValue)
            .IsRequired();
        builder.Property(x => x.OfflineAccessPolicyVersion)
            .HasDefaultValue(1u)
            .HasPrecision(10, 0)
            .IsConcurrencyToken();
    }
}
