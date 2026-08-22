using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Palladin.Module.Identity.Domain;

namespace Palladin.Module.Identity.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class LoginLockoutConfiguration : IEntityTypeConfiguration<LoginLockout>
{
    public void Configure(EntityTypeBuilder<LoginLockout> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Email).IsRequired().HasMaxLength(320);
        builder.Property(x => x.SourceIpAddresses).IsRequired();
        builder.Property(x => x.Version).IsConcurrencyToken();

        builder.HasIndex(x => x.Email).IsUnique();
    }
}
