using NodaTime;
using Palladin.Core.Types;
using Palladin.Core.Types.Exceptions;

namespace Palladin.Module.Vault.Domain;

internal sealed class VaultEntryVersion
{
    public Guid OrganizationId { get; private set; }
    public Guid VaultId { get; private set; }
    public Guid EntryId { get; private set; }
    internal EntryRevision Revision { get; private set; }
    internal MemberSequence MemberSequence { get; private set; }
    internal DiscoverySequence? DiscoverySequence { get; private set; }
    internal bool MemberIndexChanged { get; private set; }
    internal Instant ChangedAt { get; private set; }
    internal ActorType ChangedByType { get; private set; }
    internal Guid ChangedById { get; private set; }
    internal EntryOperation Operation { get; private set; }
    internal EntryKeyVersion KeyVersion { get; private set; }
    internal ushort ProtocolVersion { get; private set; }
    internal string CryptoSuiteId { get; private set; } = string.Empty;
    internal MemberKeyGeneration MemberKeyGeneration { get; private set; }
    internal byte[] MemberSecretEncodedSuitePayload { get; private set; } = [];

    internal EntryScope Scope => new(OrganizationId, VaultId, EntryId);

    private VaultEntryVersion() { }

    internal static VaultEntryVersion Create(
        MemberSecretCiphertext memberSecret,
        AllocatedVaultSequences sequences,
        bool memberIndexChanged,
        Instant changedAt,
        ActorType changedByType,
        Guid changedById)
    {
        if (sequences.MemberSequence.Value == 0
            || sequences.DiscoverySequence?.Value == 0
            || !Enum.IsDefined(changedByType)
            || changedById == Guid.Empty)
        {
            throw new DomainException(
                "Entry version sequence and authenticated actor metadata must be valid.");
        }

        return new VaultEntryVersion
        {
            OrganizationId = memberSecret.Scope.OrganizationId,
            VaultId = memberSecret.Scope.VaultId,
            EntryId = memberSecret.Scope.EntryId,
            Revision = memberSecret.Revision,
            MemberSequence = sequences.MemberSequence,
            DiscoverySequence = sequences.DiscoverySequence,
            MemberIndexChanged = memberIndexChanged,
            ChangedAt = changedAt,
            ChangedByType = changedByType,
            ChangedById = changedById,
            Operation = memberSecret.Operation,
            KeyVersion = new EntryKeyVersion(memberSecret.Header.KeyVersion),
            ProtocolVersion = memberSecret.Header.ProtocolVersion,
            CryptoSuiteId = Domain.CryptoSuiteId.XChaCha20Poly1305V1,
            MemberKeyGeneration = memberSecret.Header.MemberKeyGeneration,
            MemberSecretEncodedSuitePayload = SuitePayload.Encode(memberSecret.Header.Nonce, memberSecret.Ciphertext),
        };
    }

    internal MemberSecretCiphertext GetMemberSecret()
    {
        var header = EntryEnvelopeHeader.Create(
            ProtocolVersion,
            VaultProtocol.AlgorithmSuite,
            VaultProtocol.EntryResourceKind,
            VaultProtocol.MemberSecretProjectionKind,
            Revision.Value,
            KeyVersion.Value,
            MemberKeyGeneration,
            SuitePayload.Nonce(MemberSecretEncodedSuitePayload),
            VaultProtocol.MemberSecretProjectionKind);

        return Domain.MemberSecretCiphertext.Create(Scope, Revision, Operation, header,
            SuitePayload.Ciphertext(MemberSecretEncodedSuitePayload));
    }
}
