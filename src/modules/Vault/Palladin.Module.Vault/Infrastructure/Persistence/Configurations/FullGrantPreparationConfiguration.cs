using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Palladin.Module.Vault.Domain;

namespace Palladin.Module.Vault.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class FullGrantPreparationConfiguration : IEntityTypeConfiguration<FullGrantPreparation>
{
    public void Configure(EntityTypeBuilder<FullGrantPreparation> builder)
    {
        builder.HasKey(x => new { x.OrganizationId, x.VaultId, x.Id });
        builder.Property(x => x.AgentAccessEpoch).IsRequired();
        builder.Property(x => x.AgentPublicKey).HasMaxLength(512).IsRequired();
        builder.Property(x => x.RecipientAgentKeyVersion).IsRequired();
        builder.Property(x => x.AgentKeyFingerprint).HasMaxLength(VaultProtocol.FingerprintBytes).IsRequired();
        builder.Property(x => x.MemberKeyGeneration).IsRequired();
        builder.Property(x => x.Methods).IsRequired();
        builder.Property(x => x.GrantExpiresAt);
        builder.Property(x => x.QueryLimit);
        builder.Property(x => x.ExpirySource).HasMaxLength(16).IsRequired();
        builder.Property(x => x.CreatedBy).IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.PreparationExpiresAt).IsRequired();
        builder.HasIndex(x => x.Id).IsUnique();
        builder.HasIndex(x => new { x.OrganizationId, x.VaultId, x.AgentId }).IsUnique();
        builder.HasIndex(x => x.PreparationExpiresAt);
        builder.HasOne<Domain.Vault>()
            .WithMany()
            .HasForeignKey(x => new { x.OrganizationId, x.VaultId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Agent>()
            .WithMany()
            .HasForeignKey(x => new { x.OrganizationId, x.AgentId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
