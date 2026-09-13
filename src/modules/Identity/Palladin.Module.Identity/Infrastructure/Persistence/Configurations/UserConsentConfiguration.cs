using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Palladin.Module.Identity.Domain;

namespace Palladin.Module.Identity.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class UserConsentConfiguration : IEntityTypeConfiguration<UserConsent>
{
    public void Configure(EntityTypeBuilder<UserConsent> builder)
    {
        builder.HasKey(consent => new { consent.UserId, consent.Purpose });
        builder.Property(consent => consent.Revision).IsConcurrencyToken();
        builder.HasOne<User>().WithMany().HasForeignKey(consent => consent.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

[UsedImplicitly]
internal sealed class UserConsentHistoryConfiguration : IEntityTypeConfiguration<UserConsentHistory>
{
    public void Configure(EntityTypeBuilder<UserConsentHistory> builder)
    {
        builder.HasKey(consent => new { consent.UserId, consent.Purpose, consent.Revision });
        builder.HasIndex(consent => new { consent.UserId, consent.Purpose, consent.RequestId }).IsUnique();
        builder.HasOne<UserConsent>().WithMany()
            .HasForeignKey(consent => new { consent.UserId, consent.Purpose }).OnDelete(DeleteBehavior.Cascade);
    }
}
