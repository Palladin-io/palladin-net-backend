using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Palladin.Module.Vault.Domain;

namespace Palladin.Module.Vault.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class AgentPairingActivationCandidateConfiguration
    : IEntityTypeConfiguration<AgentPairingActivationCandidate>
{
    public void Configure(EntityTypeBuilder<AgentPairingActivationCandidate> builder)
    {
        builder.HasKey(x => new { x.ActivationId, x.VaultId });
        builder.Property(x => x.ManifestRevision)
            .HasConversion(x => (decimal)x.Value, x => new ManifestRevision((ulong)x))
            .HasPrecision(20, 0);
        builder.Property(x => x.VaultSigningKeyFingerprint).HasMaxLength(32).IsRequired();
        builder.Property(x => x.SignedManifestDigest).HasMaxLength(32).IsRequired();
        builder.HasIndex(x => new { x.OrganizationId, x.AgentId, x.VaultId, x.ManifestRevision });
        builder.HasOne<AgentPairingActivation>()
            .WithMany()
            .HasForeignKey(x => new { x.ActivationId, x.OrganizationId, x.AgentId })
            .HasPrincipalKey(x => new { x.Id, x.OrganizationId, x.AgentId })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Domain.Vault>()
            .WithMany()
            .HasForeignKey(x => new { x.OrganizationId, x.VaultId })
            .OnDelete(DeleteBehavior.Cascade);
        builder.ToTable("AgentPairingActivationCandidates");
    }
}
