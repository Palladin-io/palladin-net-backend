using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Palladin.Module.Vault.Domain;

namespace Palladin.Module.Vault.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class EncryptedReasonEnvelopeConfiguration : IEntityTypeConfiguration<EncryptedReasonEnvelope>
{
    public void Configure(EntityTypeBuilder<EncryptedReasonEnvelope> builder)
    {
        builder.HasKey(x => new { x.OrganizationId, x.VaultId, x.GrantRequestId });
        builder.Property(x => x.EntryId).IsRequired();
        builder.Property(x => x.AgentId).IsRequired();
        builder.Property(x => x.ProtocolVersion).IsRequired();
        builder.Property(x => x.CryptoSuiteId).HasMaxLength(64).IsRequired();
        builder.Property(x => x.ResourceRevision).IsRequired();
        builder.Property(x => x.ReasonKeyVersion).IsRequired();
        builder.Property(x => x.AgentMessageKeyVersion).IsRequired();
        builder.Property(x => x.MemberKeyGeneration).IsRequired();
        builder.Property(x => x.RecipientAgentMessageKeyFingerprint).HasMaxLength(VaultProtocol.FingerprintBytes);
        builder.Property(x => x.RequestedMethods).IsRequired();
        builder.Property(x => x.EncodedSuitePayload).HasMaxLength(4_120);
        builder.Property(x => x.WrapperSuiteId).HasMaxLength(64).IsRequired();
        builder.Property(x => x.AgentMessageWrappedReasonDek).HasMaxLength(120);
        builder.Property(x => x.AgentSignature).HasMaxLength(64);
        builder.HasIndex(x => new { x.OrganizationId, x.VaultId, x.EntryId });
    }
}
