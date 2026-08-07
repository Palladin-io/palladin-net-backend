using Palladin.Module.Identity.Domain;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Palladin.Module.Identity.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class OAuthConnectionConfiguration : IEntityTypeConfiguration<OAuthConnection>
{
    public void Configure(EntityTypeBuilder<OAuthConnection> builder)
    {
        builder.HasKey(x => new { x.UserId, x.Id });

        builder.HasIndex(x => new { x.Provider, x.ProviderUserId }).IsUnique();

        builder.HasOne(x => x.User)
            .WithMany(x => x.OAuthConnections)
            .HasForeignKey(x => x.UserId);
    }
}
