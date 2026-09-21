using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Palladin.Module.Vault.Domain;

namespace Palladin.Module.Vault.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class EntryShareConfiguration : IEntityTypeConfiguration<EntryShare>
{
    public void Configure(EntityTypeBuilder<EntryShare> builder)
    {
        builder.ToTable("EntryShares");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.SourceRevision)
            .HasConversion(x => (decimal)x.Value, x => new EntryRevision((ulong)x))
            .HasPrecision(20, 0);
        builder.Property(x => x.MutationVersion).IsConcurrencyToken();
        builder.Property(x => x.AccessTokenHash).HasMaxLength(32).IsRequired();
        builder.Property(x => x.Nonce).HasMaxLength(EntryShare.NonceBytes).IsRequired();
        builder.Property(x => x.Ciphertext).HasMaxLength(EntryShare.MaximumCiphertextBytes).IsRequired();
        builder.Property(x => x.ProtectedRecipientEmail).HasMaxLength(2048);
        builder.Property(x => x.SecretVerifier).HasMaxLength(1024);
        builder.HasIndex(x => new { x.OrganizationId, x.VaultId, x.EntryId, x.CreatedAt, x.Id });
        builder.HasIndex(x => new { x.ExpiresAt, x.Id })
            .HasFilter("\"RevokedAt\" IS NULL AND \"ExpiredAt\" IS NULL");
        builder.HasOne<VaultEntry>().WithMany()
            .HasForeignKey(x => new { x.OrganizationId, x.VaultId, x.EntryId })
            .OnDelete(DeleteBehavior.Cascade);
        builder.Ignore(x => x.Activities);
    }
}
