using Palladin.Module.Identity.Domain;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Palladin.Module.Identity.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.HasKey(x => x.Id);

        builder.HasIndex(x => x.Email).IsUnique();

        builder.Property(x => x.PreferredLanguage)
            .HasConversion(v => v.Code, s => PreferredLanguage.From(s))
            .HasMaxLength(10)
            .IsRequired();

        builder.Property(x => x.UpdatedAt).IsConcurrencyToken();
        builder.Property(x => x.CredentialRevision).IsConcurrencyToken();
        builder.Property(x => x.PrivateKeyWrapRevision).IsConcurrencyToken();
        builder.Property(x => x.KdfProfileId).HasMaxLength(64);
        builder.Property(x => x.DeviceWrapperMetadata).HasMaxLength(16384);

        builder.HasOne(x => x.Organization)
            .WithMany(x => x.Users)
            .HasForeignKey(x => x.OrganizationId);
    }
}
