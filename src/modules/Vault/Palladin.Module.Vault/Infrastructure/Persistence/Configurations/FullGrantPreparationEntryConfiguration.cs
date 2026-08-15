using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Palladin.Module.Vault.Domain;

namespace Palladin.Module.Vault.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class FullGrantPreparationEntryConfiguration : IEntityTypeConfiguration<FullGrantPreparationEntry>
{
    public void Configure(EntityTypeBuilder<FullGrantPreparationEntry> builder)
    {
        builder.HasKey(x => new { x.OrganizationId, x.VaultId, x.PreparationId, x.EntryId });
        builder.Property(x => x.EntryRevision).HasColumnType("numeric(20,0)");
        builder.Property(x => x.Payload).HasMaxLength(VaultProtocol.MaximumPreparedGrantEnvelopeBytes).IsRequired();
        builder.Property(x => x.PayloadDigest).HasMaxLength(32).IsRequired();
        builder.Property(x => x.PreparedAt).IsRequired();
        builder.HasOne<FullGrantPreparation>()
            .WithMany(x => x.Entries)
            .HasForeignKey(x => new { x.OrganizationId, x.VaultId, x.PreparationId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
