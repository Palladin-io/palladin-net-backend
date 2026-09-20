using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Palladin.Module.Vault.Domain;

namespace Palladin.Module.Vault.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class EntryShareSessionConfiguration : IEntityTypeConfiguration<EntryShareSession>
{
    public void Configure(EntityTypeBuilder<EntryShareSession> builder)
    {
        builder.ToTable("EntryShareSessions");
        builder.HasKey(x => new { x.ShareId, x.Id });
        builder.Property(x => x.TokenHash).HasMaxLength(32).IsRequired();
        builder.Property(x => x.OtpHash).HasMaxLength(32);
        builder.Property(x => x.MutationVersion).IsConcurrencyToken();
        builder.HasIndex(x => new { x.ExpiresAt, x.ShareId, x.Id });
        builder.HasOne<EntryShare>().WithMany().HasForeignKey(x => x.ShareId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
