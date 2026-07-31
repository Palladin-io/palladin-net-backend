using Palladin.Module.Notification.Domain;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Palladin.Module.Notification.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class PushTokenConfiguration : IEntityTypeConfiguration<PushToken>
{
    public void Configure(EntityTypeBuilder<PushToken> builder)
    {
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Token).HasMaxLength(4096);
        builder.Property(x => x.DeviceName).HasMaxLength(200);

        // A device token is per-installation, NOT per-user. It is globally unique: re-registration
        // (including by a different user after device hand-off) reassigns the single row to the
        // current owner, so push never leaks to a previous user.
        builder.HasIndex(x => x.Token).IsUnique();

        // Fan-out targeting is per organization.
        builder.HasIndex(x => x.OrganizationId);
    }
}
