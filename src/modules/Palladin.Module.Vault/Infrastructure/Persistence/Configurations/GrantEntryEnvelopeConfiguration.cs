using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Palladin.Module.Vault.Domain;

namespace Palladin.Module.Vault.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class GrantEntryEnvelopeConfiguration : IEntityTypeConfiguration<GrantEntryEnvelope>
{
    public void Configure(EntityTypeBuilder<GrantEntryEnvelope> builder)
    {
        builder.HasKey(x => new { x.OrganizationId, x.VaultId, x.GrantId, x.EntryId });
        builder.Property(x => x.GrantEnvelopeRevision).IsRequired();
        builder.Property(x => x.EntryRevision).IsRequired();
        builder.Property(x => x.ProtocolVersion).IsRequired();
        builder.Property(x => x.CryptoSuiteId).HasMaxLength(64).IsRequired();
        builder.Property(x => x.GrantKeyVersion).IsRequired();
        builder.Property(x => x.MemberKeyGeneration)
            .HasConversion(x => (decimal)x.Value, x => new MemberKeyGeneration((uint)x))
            .HasPrecision(10, 0)
            .IsRequired();
        builder.Property(x => x.RecipientAgentKeyVersion)
            .HasConversion(x => (decimal)x.Value, x => new AgentRecipientKeyVersion((uint)x))
            .HasPrecision(10, 0)
            .IsRequired();
        builder.Property(x => x.EncodedSuitePayload).HasMaxLength(262_168);
        builder.Property(x => x.AgentWrappedGrantDek).HasMaxLength(120);
        builder.Property(x => x.AgentKeyFingerprint).HasMaxLength(VaultProtocol.FingerprintBytes);
        builder.Ignore(x => x.AgentWrapperSuite);
        builder.Property(x => x.WrapperSuiteId).HasMaxLength(64).IsRequired();
        builder.Property(x => x.ExpiresAt);
        builder.Property(x => x.RemainingUses);
    }
}
