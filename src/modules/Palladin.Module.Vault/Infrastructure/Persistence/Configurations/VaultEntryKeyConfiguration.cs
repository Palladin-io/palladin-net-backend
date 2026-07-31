using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Palladin.Module.Vault.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class VaultEntryKeyConfiguration : IEntityTypeConfiguration<Domain.VaultEntryKey>
{
    public void Configure(EntityTypeBuilder<Domain.VaultEntryKey> builder)
    {
        builder.HasKey(x => new { x.OrganizationId, x.VaultId, x.EntryId, x.KeyVersion });
        builder.Property(x => x.KeyVersion)
            .HasConversion(x => (decimal)x.Value, x => new Domain.EntryKeyVersion((uint)x))
            .HasPrecision(10, 0);
        builder.Property(x => x.WrapperRevision)
            .HasConversion(x => (decimal)x.Value, x => new Domain.EntryKeyWrapperRevision((ulong)x))
            .HasPrecision(20, 0);
        builder.Property(x => x.MemberKeyGeneration)
            .HasConversion(x => (decimal)x.Value, x => new Domain.MemberKeyGeneration((uint)x))
            .HasPrecision(10, 0);
        builder.Property(x => x.WrappingKeyVersion)
            .HasConversion(x => (decimal)x.Value, x => new Domain.VaultKeyVersion((uint)x))
            .HasPrecision(10, 0);
        builder.Property(x => x.ProtocolVersion).IsRequired();
        builder.Property(x => x.CryptoSuiteId).HasMaxLength(64).IsRequired();
        builder.Property(x => x.EncodedSuitePayload).HasMaxLength(88).IsRequired();

        builder.ToTable("VaultEntryKeys");
    }
}
