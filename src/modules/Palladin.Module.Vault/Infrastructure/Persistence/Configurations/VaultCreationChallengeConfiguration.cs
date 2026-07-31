using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Palladin.Module.Vault.Domain;

namespace Palladin.Module.Vault.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class VaultCreationChallengeConfiguration : IEntityTypeConfiguration<VaultCreationChallenge>
{
    public void Configure(EntityTypeBuilder<VaultCreationChallenge> builder)
    {
        builder.HasKey(x => new { x.OrganizationId, x.VaultId });
        builder.Property(x => x.RequestedBy).IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.ExpiresAt).IsRequired();
        builder.Property(x => x.ConsumedAt).IsConcurrencyToken();
        builder.HasIndex(x => new { x.OrganizationId, x.RequestedBy }).IsUnique();
        builder.HasIndex(x => x.ExpiresAt);
    }
}
