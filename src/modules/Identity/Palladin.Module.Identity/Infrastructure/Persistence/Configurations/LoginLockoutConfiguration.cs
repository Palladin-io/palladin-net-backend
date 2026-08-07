using Palladin.Module.Identity.Domain;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Palladin.Module.Identity.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class LoginLockoutConfiguration : IEntityTypeConfiguration<LoginLockout>
{
    public void Configure(EntityTypeBuilder<LoginLockout> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Email).IsRequired().HasMaxLength(320);
        builder.Property(x => x.IpAddress).IsRequired().HasMaxLength(64);

        builder.HasIndex(x => new { x.Email, x.IpAddress }).IsUnique();
    }
}
