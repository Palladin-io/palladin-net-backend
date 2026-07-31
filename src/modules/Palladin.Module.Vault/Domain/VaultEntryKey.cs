using Palladin.Core.Types.Exceptions;

namespace Palladin.Module.Vault.Domain;

internal sealed class VaultEntryKey
{
    public Guid OrganizationId { get; private set; }
    public Guid VaultId { get; private set; }
    public Guid EntryId { get; private set; }
    internal EntryKeyVersion KeyVersion { get; private set; }
    internal EntryKeyWrapperRevision WrapperRevision { get; private set; }
    internal MemberKeyGeneration MemberKeyGeneration { get; private set; }
    internal VaultKeyVersion WrappingKeyVersion { get; private set; }
    internal ushort ProtocolVersion { get; private set; }
    internal string CryptoSuiteId { get; private set; } = string.Empty;
    internal byte[] EncodedSuitePayload { get; private set; } = [];

    internal EntryScope Scope => new(OrganizationId, VaultId, EntryId);

    private VaultEntryKey() { }

    internal static VaultEntryKey Create(
        EntryScope scope,
        EntryKeyWrapperRevision wrapperRevision,
        EntryKeyVersion keyVersion,
        MemberKeyGeneration memberKeyGeneration,
        VaultKeyVersion wrappingKeyVersion,
        EntryEnvelopeHeader header,
        byte[] wrappedEntryDekByVk)
    {
        scope.Validate();
        if (header.ProjectionKind != VaultProtocol.WrappedKeyProjectionKind
            || header.ResourceRevision != wrapperRevision.Value
            || header.KeyVersion != keyVersion.Value
            || header.MemberKeyGeneration != memberKeyGeneration)
        {
            throw new DomainException("Wrapped Entry key header does not match its structural identity.");
        }

        if (wrappedEntryDekByVk.Length is < 16 or > VaultProtocol.MaximumWrappedEntryKeyBytes)
        {
            throw new DomainException("Wrapped Entry key size is outside the protocol limit.");
        }

        return new VaultEntryKey
        {
            OrganizationId = scope.OrganizationId,
            VaultId = scope.VaultId,
            EntryId = scope.EntryId,
            KeyVersion = keyVersion,
            WrapperRevision = wrapperRevision,
            MemberKeyGeneration = memberKeyGeneration,
            WrappingKeyVersion = wrappingKeyVersion,
            ProtocolVersion = header.ProtocolVersion,
            CryptoSuiteId = Domain.CryptoSuiteId.XChaCha20Poly1305V1,
            EncodedSuitePayload = SuitePayload.Encode(header.Nonce, wrappedEntryDekByVk),
        };
    }

    internal EntryEnvelopeHeader GetHeader() => EntryEnvelopeHeader.Create(
        ProtocolVersion,
        VaultProtocol.AlgorithmSuite,
        VaultProtocol.EntryResourceKind,
        VaultProtocol.WrappedKeyProjectionKind,
        WrapperRevision.Value,
        KeyVersion.Value,
        MemberKeyGeneration,
        SuitePayload.Nonce(EncodedSuitePayload),
        VaultProtocol.WrappedKeyProjectionKind);

    internal bool HasSameContent(VaultEntryKey other) =>
        Scope == other.Scope
        && KeyVersion == other.KeyVersion
        && WrapperRevision == other.WrapperRevision
        && MemberKeyGeneration == other.MemberKeyGeneration
        && WrappingKeyVersion == other.WrappingKeyVersion
        && EntryCiphertextEquality.HeaderEquals(GetHeader(), other.GetHeader())
        && EncodedSuitePayload.AsSpan().SequenceEqual(other.EncodedSuitePayload);

    internal void ApplyRotationRewrap(VaultEntryKey replacement)
    {
        if (Scope != replacement.Scope
            || KeyVersion != replacement.KeyVersion
            || replacement.WrapperRevision.Value != checked(WrapperRevision.Value + 1))
        {
            throw new DomainException("Rotated Entry key wrapper does not advance the current wrapper exactly once.");
        }

        WrapperRevision = replacement.WrapperRevision;
        MemberKeyGeneration = replacement.MemberKeyGeneration;
        WrappingKeyVersion = replacement.WrappingKeyVersion;
        ProtocolVersion = replacement.ProtocolVersion;
        CryptoSuiteId = replacement.CryptoSuiteId;
        EncodedSuitePayload = replacement.EncodedSuitePayload.ToArray();
    }
}
