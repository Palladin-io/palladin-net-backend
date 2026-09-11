using Palladin.Module.Identity.Domain;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Palladin.Module.Identity.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class WaitlistEntryConfiguration : IEntityTypeConfiguration<WaitlistEntry>
{
    public void Configure(EntityTypeBuilder<WaitlistEntry> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Email).IsRequired().HasMaxLength(320);
        builder.Property(x => x.Language).IsRequired().HasMaxLength(2);
        builder.Property(x => x.TokenHash).IsRequired().HasMaxLength(64).IsConcurrencyToken();
        builder.Property(x => x.VerifiedAt).IsConcurrencyToken();

        // Normalized email is the business key; token hash is the verification lookup.
        builder.HasIndex(x => x.Email).IsUnique();
        builder.HasIndex(x => x.TokenHash);
        builder.HasIndex(x => x.DeveloperBenefitUserId).IsUnique();
    }
}
