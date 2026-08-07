using Palladin.Core.Security;
using Palladin.Module.Notification.Domain;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Palladin.Module.Notification.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.HasKey(x => x.UserId);

        builder.Property(x => x.Name).HasMaxLength(256);
        builder.Property(x => x.Email).HasMaxLength(320);

        builder.Property(x => x.Permissions).HasDefaultValue(Permission.None);
    }
}
