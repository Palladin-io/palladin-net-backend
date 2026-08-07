using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Palladin.Module.Vault.Domain;

namespace Palladin.Module.Vault.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class VaultMemberKeyEnvelopeConfiguration : IEntityTypeConfiguration<VaultMemberKeyEnvelope>
{
    public void Configure(EntityTypeBuilder<VaultMemberKeyEnvelope> builder)
    {
        builder.HasKey(x => new { x.OrganizationId, x.VaultId, x.MemberId, x.MemberKeyGeneration });

        builder.Property(x => x.ProtocolVersion).IsRequired();
        builder.Property(x => x.WrapperSuiteId).HasMaxLength(64).IsRequired();
        builder.Property(x => x.RecipientKeyFingerprint).IsRequired();
        builder.Property(x => x.SealedVaultKeyPackage).IsRequired();
        builder.Property(x => x.MemberKeyGeneration)
            .HasConversion(x => (decimal)x.Value, x => new MemberKeyGeneration((uint)x))
            .HasPrecision(10, 0);
        builder.Property(x => x.VaultKeyVersion)
            .HasConversion(x => (decimal)x.Value, x => new VaultKeyVersion((uint)x))
            .HasPrecision(10, 0);
        builder.Property(x => x.RecipientKeyVersion)
            .HasConversion(x => (decimal)x.Value, x => new MemberRecipientKeyVersion((uint)x))
            .HasPrecision(10, 0);

        builder.HasOne(x => x.Vault)
            .WithMany(x => x.VaultMemberKeyEnvelopes)
            .HasForeignKey(x => new { x.OrganizationId, x.VaultId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
