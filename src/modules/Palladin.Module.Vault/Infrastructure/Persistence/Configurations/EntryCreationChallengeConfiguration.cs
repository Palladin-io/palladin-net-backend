using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Palladin.Module.Vault.Domain;

namespace Palladin.Module.Vault.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class EntryCreationChallengeConfiguration : IEntityTypeConfiguration<EntryCreationChallenge>
{
    public void Configure(EntityTypeBuilder<EntryCreationChallenge> builder)
    {
        builder.HasKey(x => new { x.OrganizationId, x.VaultId, x.EntryId });
        builder.Property(x => x.RequestedBy).IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.ExpiresAt).IsRequired();
        builder.Property(x => x.ConsumedAt).IsConcurrencyToken();
        builder.HasIndex(x => new { x.OrganizationId, x.VaultId, x.RequestedBy });
        builder.HasIndex(x => x.ExpiresAt);
        builder.HasOne<Domain.Vault>()
            .WithMany()
            .HasForeignKey(x => new { x.OrganizationId, x.VaultId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
