using Palladin.Module.Identity.Domain;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Palladin.Module.Identity.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class PasswordCredentialConfiguration : IEntityTypeConfiguration<PasswordCredential>
{
    public void Configure(EntityTypeBuilder<PasswordCredential> builder)
    {
        builder.HasKey(x => x.UserId);
        builder.Property(x => x.AuthHash).IsRequired();
        builder.Property(x => x.AuthSalt).IsRequired();
        builder.Property(x => x.ServerHashSalt).IsRequired();
        builder.Property(x => x.UpdatedAt).IsConcurrencyToken();

        builder.HasOne(x => x.User)
            .WithOne(u => u.PasswordCredential)
            .HasForeignKey<PasswordCredential>(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
