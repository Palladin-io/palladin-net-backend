using Palladin.Module.Identity.Domain;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Palladin.Module.Identity.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class TotpCredentialConfiguration : IEntityTypeConfiguration<TotpCredential>
{
    public void Configure(EntityTypeBuilder<TotpCredential> builder)
    {
        builder.HasKey(x => x.UserId);
        builder.Property(x => x.PendingSecret).HasMaxLength(128);
        builder.Property(x => x.Secret).HasMaxLength(128);
        builder.Property(x => x.ConfigurationRevision).HasDefaultValue(1u).IsConcurrencyToken();

        builder.HasOne(x => x.User)
            .WithOne(u => u.TotpCredential)
            .HasForeignKey<TotpCredential>(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(x => x.RecoveryCodes)
            .WithOne(c => c.Credential)
            .HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
