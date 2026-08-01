using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Palladin.Module.Vault.Domain;

namespace Palladin.Module.Vault.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class GrantEntryScopeConfiguration : IEntityTypeConfiguration<GrantEntryScope>
{
    public void Configure(EntityTypeBuilder<GrantEntryScope> builder)
    {
        builder.HasKey(x => new { x.OrganizationId, x.VaultId, x.GrantId, x.EntryId });
        builder.Property(x => x.Methods).IsRequired();
        builder.Property(x => x.DeliveryPolicy).IsRequired();
        builder.Property(x => x.FieldIds).HasMaxLength(32_768);

        builder.HasOne<VaultEntry>()
            .WithMany()
            .HasForeignKey(x => new { x.OrganizationId, x.VaultId, x.EntryId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.VaultId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Envelope)
            .WithOne()
            .HasForeignKey<GrantEntryEnvelope>(x => new { x.OrganizationId, x.VaultId, x.GrantId, x.EntryId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
