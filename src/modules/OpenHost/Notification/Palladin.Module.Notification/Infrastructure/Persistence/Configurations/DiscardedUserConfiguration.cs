using Palladin.Module.Notification.Domain;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Palladin.Module.Notification.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class DiscardedUserConfiguration : IEntityTypeConfiguration<DiscardedUser>
{
    public void Configure(EntityTypeBuilder<DiscardedUser> builder) => builder.HasKey(x => x.UserId);
}
