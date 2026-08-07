using Palladin.Module.Identity.Domain;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Palladin.Module.Identity.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.HasKey(x => new { x.UserId, x.Id });

        builder.HasIndex(x => x.TokenHash);

        builder.HasOne(x => x.User)
            .WithMany(x => x.RefreshTokens)
            .HasForeignKey(x => x.UserId);
    }
}
