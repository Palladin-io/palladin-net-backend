using Palladin.Module.Vault.Domain;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Palladin.Module.Vault.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class AgentConfiguration : IEntityTypeConfiguration<Agent>
{
    public void Configure(EntityTypeBuilder<Agent> builder)
    {
        builder.HasKey(x => x.Id);

        builder.Property(x => x.PublicKey).HasMaxLength(512);
        builder.Property(x => x.SigningPublicKey).HasMaxLength(512);
        builder.Property(x => x.RecipientKeyVersion).HasDefaultValue(1u);
        builder.Property(x => x.Name).HasMaxLength(200);
        builder.Property(x => x.IconKey).HasMaxLength(500);
        builder.Property(x => x.IconColor).HasMaxLength(32);
        builder.Property(x => x.AccessEpoch).HasDefaultValue(0u);
        builder.Property(x => x.LastProcessedDeactivationEpoch).HasDefaultValue(0u);
        builder.Property(x => x.MutationVersion)
            .HasConversion(x => (decimal)x, x => (ulong)x)
            .HasPrecision(20, 0)
            .IsConcurrencyToken();

        builder.HasIndex(x => new { x.OrganizationId, x.Status });
        builder.HasAlternateKey(x => new { x.OrganizationId, x.Id });
    }
}
