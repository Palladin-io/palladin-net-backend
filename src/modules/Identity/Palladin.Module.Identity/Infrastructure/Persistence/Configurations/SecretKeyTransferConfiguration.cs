using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Palladin.Core.Cryptography;
using Palladin.Module.Identity.Domain;

namespace Palladin.Module.Identity.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class SecretKeyTransferConfiguration : IEntityTypeConfiguration<SecretKeyTransfer>
{
    public void Configure(EntityTypeBuilder<SecretKeyTransfer> builder)
    {
        builder.HasKey(transfer => transfer.Id);
        builder.HasIndex(transfer => new { transfer.ExpiresAt, transfer.Id });
        builder.Property(transfer => transfer.Revision).IsConcurrencyToken();
        builder.Property(transfer => transfer.Challenge).HasMaxLength(SecretKeyTransfer.ChallengeBytes).IsRequired();
        builder.Property(transfer => transfer.PollTokenHash).HasMaxLength(32);
        builder.Property(transfer => transfer.RecipientPublicKey).HasMaxLength(XWingKem.PublicKeyBytes);
        builder.Property(transfer => transfer.KemCiphertext).HasMaxLength(XWingKem.CiphertextBytes);
        builder.Property(transfer => transfer.SealedSecret).HasMaxLength(SecretKeyTransfer.SealedSecretBytes);
        builder.HasOne<User>().WithMany().HasForeignKey(transfer => transfer.SenderUserId).OnDelete(DeleteBehavior.Cascade);
    }
}
