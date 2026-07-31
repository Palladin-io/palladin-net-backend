using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Palladin.Module.Vault.Domain;

namespace Palladin.Module.Vault.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class MemberKeyDirectoryEntryConfiguration : IEntityTypeConfiguration<MemberKeyDirectoryEntry>
{
    public void Configure(EntityTypeBuilder<MemberKeyDirectoryEntry> builder)
    {
        builder.HasKey(x => x.UserId);
        builder.Property(x => x.KeyVersion)
            .HasConversion(x => (decimal)x.Value, x => new MemberRecipientKeyVersion((uint)x))
            .HasPrecision(10, 0);
        builder.Property(x => x.Fingerprint).IsRequired();
        builder.Property(x => x.PublicKey).HasMaxLength(VaultProtocol.FingerprintBytes).IsRequired();
        builder.Property(x => x.UpdatedAt).IsConcurrencyToken();
    }
}
