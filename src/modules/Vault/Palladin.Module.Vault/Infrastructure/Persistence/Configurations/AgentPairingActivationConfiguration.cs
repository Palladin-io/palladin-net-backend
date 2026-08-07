using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Palladin.Module.Vault.Domain;

namespace Palladin.Module.Vault.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class AgentPairingActivationConfiguration : IEntityTypeConfiguration<AgentPairingActivation>
{
    public void Configure(EntityTypeBuilder<AgentPairingActivation> builder)
    {
        builder.HasKey(x => x.Id);
        builder.HasAlternateKey(x => new { x.Id, x.OrganizationId, x.AgentId });
        builder.Property(x => x.AgentAccessEpoch).IsRequired();
        builder.Property(x => x.AgentX25519Fingerprint).HasMaxLength(32).IsRequired();
        builder.Property(x => x.AgentEd25519Fingerprint).HasMaxLength(32).IsRequired();
        builder.Property(x => x.CandidateDigest).HasMaxLength(32).IsRequired();
        builder.Property(x => x.CandidateVaultCount).IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.ExpiresAt).IsRequired();
        builder.Property(x => x.ConfirmedAt).IsConcurrencyToken();
        builder.Property(x => x.ConfirmedBy);
        builder.HasIndex(x => new { x.OrganizationId, x.AgentId, x.AgentAccessEpoch, x.CreatedAt });
        builder.HasIndex(x => x.ExpiresAt);
        builder.HasOne<Agent>()
            .WithMany()
            .HasForeignKey(x => new { x.OrganizationId, x.AgentId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
            .OnDelete(DeleteBehavior.Cascade);
        builder.ToTable("AgentPairingActivations");
    }
}
