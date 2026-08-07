using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Palladin.Module.Vault.Domain;

namespace Palladin.Module.Vault.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class VaultKeyMaterialEnvelopeConfiguration : IEntityTypeConfiguration<VaultKeyMaterialEnvelope>
{
    public void Configure(EntityTypeBuilder<VaultKeyMaterialEnvelope> builder)
    {
        builder.HasKey(x => new { x.OrganizationId, x.VaultId, x.Kind });
        builder.Property(x => x.Revision).HasConversion<decimal>().HasPrecision(20, 0);
        builder.Property(x => x.CryptoSuiteId).HasMaxLength(64).IsRequired();
        builder.Property(x => x.KeyVersion).HasConversion<decimal>().HasPrecision(10, 0);
        builder.Property(x => x.MemberKeyGeneration)
            .HasConversion(x => (decimal)x.Value, x => new MemberKeyGeneration((uint)x))
            .HasPrecision(10, 0);
        builder.Property(x => x.WrappingKeyVersion)
            .HasConversion(x => (decimal)x.Value, x => new VaultKeyVersion((uint)x))
            .HasPrecision(10, 0);
        builder.Property(x => x.EncodedSuitePayload).IsRequired()
            .HasMaxLength(VaultProtocol.NonceBytes + VaultProtocol.MaximumVaultKeyMaterialCiphertextBytes);
        builder.HasOne<Domain.Vault>()
            .WithMany(x => x.KeyMaterialEnvelopes)
            .HasForeignKey(x => new { x.OrganizationId, Id = x.VaultId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
