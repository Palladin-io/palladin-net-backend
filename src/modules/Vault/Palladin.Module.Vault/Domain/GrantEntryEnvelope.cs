using Palladin.Core.Types.Exceptions;
using NodaTime;

namespace Palladin.Module.Vault.Domain;

internal sealed class GrantEntryEnvelope
{
    internal Guid OrganizationId { get; private set; }
    internal Guid VaultId { get; private set; }
    internal Guid GrantId { get; private set; }
    internal Guid EntryId { get; private set; }
    internal ulong GrantEnvelopeRevision { get; private set; }
    internal ulong EntryRevision { get; private set; }
    internal ushort ProtocolVersion { get; private set; }
    internal string CryptoSuiteId { get; private set; } = string.Empty;
    internal uint GrantKeyVersion { get; private set; }
    internal MemberKeyGeneration MemberKeyGeneration { get; private set; }
    internal AgentRecipientKeyVersion RecipientAgentKeyVersion { get; private set; }
    internal byte[] EncodedSuitePayload { get; private set; } = [];
    internal byte[] AgentWrappedGrantDek { get; private set; } = [];
    internal string WrapperSuiteId { get; private set; } = string.Empty;
    internal ushort AgentWrapperSuite => 1;
    internal byte[] AgentKeyFingerprint { get; private set; } = [];
    internal Instant? ExpiresAt { get; private set; }
    internal int? RemainingUses { get; private set; }

    private GrantEntryEnvelope() { }

    internal static GrantEntryEnvelope Create(
        EntryScope scope,
        Guid grantId,
        ulong envelopeRevision,
        EntryRevision entryRevision,
        uint grantKeyVersion,
        MemberKeyGeneration memberKeyGeneration,
        AgentRecipientKeyVersion recipientAgentKeyVersion,
        byte[] ciphertext,
        byte[] nonce,
        byte[] wrappedDek,
        ushort wrapperSuite,
        byte[] agentKeyFingerprint,
        Instant? expiresAt,
        int? remainingUses)
    {
        if (grantId == Guid.Empty || entryRevision.Value == 0
            || envelopeRevision == 0 || grantKeyVersion == 0 || ciphertext.Length is < 16 or > 262_144
            || nonce.Length != VaultProtocol.NonceBytes || wrappedDek.Length is < 16 or > 512
            || agentKeyFingerprint.Length != VaultProtocol.FingerprintBytes || wrapperSuite == 0
            || remainingUses is <= 0 || (expiresAt.HasValue && remainingUses.HasValue))
        {
            throw new DomainException("Grant envelope is invalid.");
        }

        scope.Validate();
        return new GrantEntryEnvelope
        {
            OrganizationId = scope.OrganizationId,
            VaultId = scope.VaultId,
            GrantId = grantId,
            EntryId = scope.EntryId,
            GrantEnvelopeRevision = envelopeRevision,
            EntryRevision = entryRevision.Value,
            ProtocolVersion = VaultProtocol.CurrentVersion,
            CryptoSuiteId = Domain.CryptoSuiteId.XChaCha20Poly1305V1,
            GrantKeyVersion = grantKeyVersion,
            MemberKeyGeneration = memberKeyGeneration,
            RecipientAgentKeyVersion = recipientAgentKeyVersion,
            EncodedSuitePayload = SuitePayload.Encode(nonce, ciphertext),
            AgentWrappedGrantDek = wrappedDek.ToArray(),
            WrapperSuiteId = Infrastructure.Crypto.X25519SealedBoxContract.SuiteId,
            AgentKeyFingerprint = agentKeyFingerprint.ToArray(),
            ExpiresAt = expiresAt,
            RemainingUses = remainingUses,
        };
    }

    internal void ValidateScope(EntryScope scope, Guid grantId)
    {
        if (OrganizationId != scope.OrganizationId || VaultId != scope.VaultId
            || EntryId != scope.EntryId || GrantId != grantId)
        {
            throw new DomainException("Grant envelope scope does not match the durable grant scope.");
        }
    }

    internal void RefreshFrom(GrantEntryEnvelope next)
    {
        if (OrganizationId != next.OrganizationId || VaultId != next.VaultId
            || GrantId != next.GrantId || EntryId != next.EntryId)
        {
            throw new DomainException("Grant envelope scope does not match the durable grant scope.");
        }

        GrantEnvelopeRevision = next.GrantEnvelopeRevision;
        EntryRevision = next.EntryRevision;
        ProtocolVersion = next.ProtocolVersion;
        CryptoSuiteId = next.CryptoSuiteId;
        GrantKeyVersion = next.GrantKeyVersion;
        MemberKeyGeneration = next.MemberKeyGeneration;
        RecipientAgentKeyVersion = next.RecipientAgentKeyVersion;
        EncodedSuitePayload = next.EncodedSuitePayload.ToArray();
        AgentWrappedGrantDek = next.AgentWrappedGrantDek.ToArray();
        WrapperSuiteId = next.WrapperSuiteId;
        AgentKeyFingerprint = next.AgentKeyFingerprint.ToArray();
        ExpiresAt = next.ExpiresAt;
        RemainingUses = next.RemainingUses;
    }
}
