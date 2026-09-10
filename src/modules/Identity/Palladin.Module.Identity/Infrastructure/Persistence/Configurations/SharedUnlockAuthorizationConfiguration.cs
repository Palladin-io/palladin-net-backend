using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Palladin.Module.Identity.Domain;

namespace Palladin.Module.Identity.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class SharedUnlockAuthorizationConfiguration : IEntityTypeConfiguration<SharedUnlockAuthorization>
{
    public void Configure(EntityTypeBuilder<SharedUnlockAuthorization> builder)
    {
        builder.HasKey(authorization => new { authorization.UserId, authorization.SessionId });
        builder.Property(authorization => authorization.Sequence).IsConcurrencyToken();
        builder.Property(authorization => authorization.LinkId).IsConcurrencyToken();
        builder.Property(authorization => authorization.LinkEpoch).IsConcurrencyToken();
        builder.HasOne<User>().WithMany().HasForeignKey(authorization => authorization.UserId);
    }
}
