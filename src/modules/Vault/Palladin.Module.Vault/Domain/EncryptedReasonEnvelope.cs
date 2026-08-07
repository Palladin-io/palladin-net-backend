using Palladin.Core.Types.Exceptions;
using Palladin.Core.Types;

namespace Palladin.Module.Vault.Domain;

internal sealed class EncryptedReasonEnvelope
{
    internal Guid OrganizationId { get; private set; }
    internal Guid VaultId { get; private set; }
    internal Guid EntryId { get; private set; }
    internal Guid GrantRequestId { get; private set; }
    internal Guid AgentId { get; private set; }
    internal ushort ProtocolVersion { get; private set; }
    internal string CryptoSuiteId { get; private set; } = string.Empty;
    internal ulong ResourceRevision { get; private set; }
    internal uint ReasonKeyVersion { get; private set; }
    internal uint AgentMessageKeyVersion { get; private set; }
    internal uint MemberKeyGeneration { get; private set; }
    internal byte[] RecipientAgentMessageKeyFingerprint { get; private set; } = [];
    internal GrantMethods RequestedMethods { get; private set; }
    internal byte[] EncodedSuitePayload { get; private set; } = [];
    internal string WrapperSuiteId { get; private set; } = string.Empty;
    internal byte[] AgentMessageWrappedReasonDek { get; private set; } = [];
    internal byte[] AgentSignature { get; private set; } = [];

    private EncryptedReasonEnvelope() { }

    internal static EncryptedReasonEnvelope Create(
        EntryScope scope,
        Guid requestId,
        Guid agentId,
        ulong resourceRevision,
        uint reasonKeyVersion,
        uint agentMessageKeyVersion,
        uint memberKeyGeneration,
        byte[] recipientAgentMessageKeyFingerprint,
        GrantMethods requestedMethods,
        byte[] ciphertext,
        byte[] nonce,
        byte[] wrappedReasonDek,
        byte[] signature)
    {
        scope.Validate();
        if (requestId == Guid.Empty || agentId == Guid.Empty || resourceRevision != 1
            || reasonKeyVersion != 1 || agentMessageKeyVersion == 0 || memberKeyGeneration == 0
            || recipientAgentMessageKeyFingerprint.Length != VaultProtocol.FingerprintBytes
            || !requestedMethods.IsValidSet()
            || ciphertext.Length is < 16 or > 4_096 || nonce.Length != VaultProtocol.NonceBytes
            || wrappedReasonDek.Length is < 16 or > 128 || signature.Length != 64)
        {
            throw new DomainException("Encrypted reason envelope is invalid.");
        }

        return new EncryptedReasonEnvelope
        {
            OrganizationId = scope.OrganizationId,
            VaultId = scope.VaultId,
            EntryId = scope.EntryId,
            GrantRequestId = requestId,
            AgentId = agentId,
            ProtocolVersion = VaultProtocol.CurrentVersion,
            CryptoSuiteId = Domain.CryptoSuiteId.XChaCha20Poly1305V1,
            ResourceRevision = resourceRevision,
            ReasonKeyVersion = reasonKeyVersion,
            AgentMessageKeyVersion = agentMessageKeyVersion,
            MemberKeyGeneration = memberKeyGeneration,
            RecipientAgentMessageKeyFingerprint = recipientAgentMessageKeyFingerprint.ToArray(),
            RequestedMethods = requestedMethods,
            EncodedSuitePayload = SuitePayload.Encode(nonce, ciphertext),
            WrapperSuiteId = Infrastructure.Crypto.X25519SealedBoxContract.SuiteId,
            AgentMessageWrappedReasonDek = wrappedReasonDek.ToArray(),
            AgentSignature = signature.ToArray(),
        };
    }
}
