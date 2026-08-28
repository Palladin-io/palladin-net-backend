using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Palladin.Module.Vault.Domain;

namespace Palladin.Module.Vault.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class ScriptExecutionPackageConfiguration : IEntityTypeConfiguration<ScriptExecutionPackage>
{
    public void Configure(EntityTypeBuilder<ScriptExecutionPackage> builder)
    {
        builder.HasKey(x => new { x.OrganizationId, x.VaultId, x.GrantId });
        builder.Property(x => x.AgentId).IsRequired();
        builder.Property(x => x.AgentAccessEpoch).IsRequired();
        builder.Property(x => x.ScriptEntryId).IsRequired();
        builder.Property(x => x.ScriptRevision).IsRequired();
        builder.Property(x => x.PackageRevision).IsRequired();
        builder.Property(x => x.ContractVersion).IsRequired();
        builder.Property(x => x.RecipientAgentKeyVersion).IsRequired();
        builder.Property(x => x.RecipientAgentKeyFingerprint).HasMaxLength(VaultProtocol.FingerprintBytes);
        builder.Property(x => x.VaultSigningKeyVersion).IsRequired();
        builder.Property(x => x.VaultSigningKeyFingerprint).HasMaxLength(VaultProtocol.FingerprintBytes);
        builder.Property(x => x.ManifestDigest).HasMaxLength(32);
        builder.Property(x => x.EncodedPackageCiphertext).HasMaxLength(2_097_152);
        builder.Property(x => x.ProducerSignature).HasMaxLength(64);
    }
}
