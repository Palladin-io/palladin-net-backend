using Palladin.Core.Types.Exceptions;

namespace Palladin.Module.Vault.Domain;

internal sealed record VaultKeyMaterialHeader(
    ushort ProtocolVersion,
    ushort AlgorithmSuite,
    ushort ResourceKind,
    ushort ProjectionKind,
    ulong ResourceRevision,
    uint KeyVersion,
    MemberKeyGeneration MemberKeyGeneration,
    byte[] Nonce);

internal sealed class VaultKeyMaterialEnvelope
{
    internal Guid OrganizationId { get; private set; }
    internal Guid VaultId { get; private set; }
    internal VaultKeyMaterialKind Kind { get; private set; }
    internal ulong Revision { get; private set; }
    internal uint KeyVersion { get; private set; }
    internal MemberKeyGeneration MemberKeyGeneration { get; private set; }
    internal VaultKeyVersion WrappingKeyVersion { get; private set; }
    internal string CryptoSuiteId { get; private set; } = string.Empty;
    internal byte[] EncodedSuitePayload { get; private set; } = [];

    private VaultKeyMaterialEnvelope() { }

    internal static VaultKeyMaterialEnvelope Create(
        VaultScope scope,
        VaultKeyMaterialKind kind,
        ulong revision,
        uint keyVersion,
        MemberKeyGeneration memberKeyGeneration,
        VaultKeyVersion wrappingKeyVersion,
        VaultKeyMaterialHeader header,
        byte[] ciphertext)
    {
        scope.Validate();
        var expectedProjection = kind == VaultKeyMaterialKind.DiscoveryKey
            ? VaultProtocol.VaultDiscoveryKeyProjectionKind
            : VaultProtocol.VaultPrivateKeyProjectionKind;
        if (!Enum.IsDefined(kind)
            || revision == 0
            || keyVersion == 0
            || header.ProtocolVersion != VaultProtocol.CurrentVersion
            || header.AlgorithmSuite != VaultProtocol.AlgorithmSuite
            || header.ResourceKind != VaultProtocol.VaultResourceKind
            || header.ProjectionKind != expectedProjection
            || header.ResourceRevision != revision
            || header.KeyVersion != keyVersion
            || header.MemberKeyGeneration != memberKeyGeneration
            || header.Nonce.Length != VaultProtocol.NonceBytes
            || ciphertext.Length is < 16 or > VaultProtocol.MaximumVaultKeyMaterialCiphertextBytes)
        {
            throw new DomainException("Encrypted Vault key material does not match the canonical protocol envelope.");
        }

        return new VaultKeyMaterialEnvelope
        {
            OrganizationId = scope.OrganizationId,
            VaultId = scope.VaultId,
            Kind = kind,
            Revision = revision,
            KeyVersion = keyVersion,
            MemberKeyGeneration = memberKeyGeneration,
            WrappingKeyVersion = wrappingKeyVersion,
            CryptoSuiteId = Domain.CryptoSuiteId.XChaCha20Poly1305V1,
            EncodedSuitePayload = SuitePayload.Encode(header.Nonce, ciphertext),
        };
    }

    internal VaultKeyMaterialHeader GetHeader() => new(
        VaultProtocol.CurrentVersion,
        VaultProtocol.AlgorithmSuite,
        VaultProtocol.VaultResourceKind,
        Kind == VaultKeyMaterialKind.DiscoveryKey
            ? VaultProtocol.VaultDiscoveryKeyProjectionKind
            : VaultProtocol.VaultPrivateKeyProjectionKind,
        Revision,
        KeyVersion,
        MemberKeyGeneration,
        SuitePayload.Nonce(EncodedSuitePayload));

    internal void ReplaceWith(VaultKeyMaterialEnvelope replacement)
    {
        if (replacement.OrganizationId != OrganizationId
            || replacement.VaultId != VaultId
            || replacement.Kind != Kind
            || replacement.Revision != checked(Revision + 1))
        {
            throw new DomainException("Rotated Vault key material must advance the same envelope by one revision.");
        }

        Revision = replacement.Revision;
        KeyVersion = replacement.KeyVersion;
        MemberKeyGeneration = replacement.MemberKeyGeneration;
        WrappingKeyVersion = replacement.WrappingKeyVersion;
        CryptoSuiteId = replacement.CryptoSuiteId;
        EncodedSuitePayload = replacement.EncodedSuitePayload.ToArray();
    }
}
