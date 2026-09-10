using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Palladin.Module.Identity.Domain;

namespace Palladin.Module.Identity.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class SharedUnlockLinkConfiguration : IEntityTypeConfiguration<SharedUnlockLink>
{
    public void Configure(EntityTypeBuilder<SharedUnlockLink> builder)
    {
        builder.HasKey(link => new { link.UserId, link.Id });
        builder.Property(link => link.Revision).IsConcurrencyToken();
        builder.HasOne<User>().WithMany().HasForeignKey(link => link.UserId);
    }
}
