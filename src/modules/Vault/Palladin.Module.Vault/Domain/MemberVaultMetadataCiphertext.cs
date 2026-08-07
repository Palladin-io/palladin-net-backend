using Palladin.Core.Types.Exceptions;

namespace Palladin.Module.Vault.Domain;

internal sealed record MemberVaultMetadataHeader
{
    internal ushort ProtocolVersion { get; }
    internal ushort AlgorithmSuite { get; }
    internal ushort ResourceKind { get; }
    internal ushort ProjectionKind { get; }
    internal MetadataRevision ResourceRevision { get; }
    internal VaultKeyVersion KeyVersion { get; }
    internal MemberKeyGeneration MemberKeyGeneration { get; }
    internal byte[] Nonce { get; }

    private MemberVaultMetadataHeader(
        ushort protocolVersion,
        ushort algorithmSuite,
        ushort resourceKind,
        ushort projectionKind,
        MetadataRevision resourceRevision,
        VaultKeyVersion keyVersion,
        MemberKeyGeneration memberKeyGeneration,
        byte[] nonce)
    {
        ProtocolVersion = protocolVersion;
        AlgorithmSuite = algorithmSuite;
        ResourceKind = resourceKind;
        ProjectionKind = projectionKind;
        ResourceRevision = resourceRevision;
        KeyVersion = keyVersion;
        MemberKeyGeneration = memberKeyGeneration;
        Nonce = nonce.ToArray();
    }

    internal static MemberVaultMetadataHeader Create(
        ushort protocolVersion,
        ushort algorithmSuite,
        ushort resourceKind,
        ushort projectionKind,
        MetadataRevision resourceRevision,
        VaultKeyVersion keyVersion,
        MemberKeyGeneration memberKeyGeneration,
        byte[] nonce)
    {
        if (protocolVersion != VaultProtocol.CurrentVersion
            || algorithmSuite != VaultProtocol.AlgorithmSuite
            || resourceKind != VaultProtocol.VaultResourceKind
            || projectionKind != VaultProtocol.MemberVaultMetadataProjectionKind)
        {
            throw new DomainException("Member Vault metadata header is incompatible with Vault protocol 2.");
        }

        if (nonce.Length != VaultProtocol.NonceBytes)
        {
            throw new DomainException("Member Vault metadata nonce must contain exactly 24 bytes.");
        }

        return new MemberVaultMetadataHeader(
            protocolVersion,
            algorithmSuite,
            resourceKind,
            projectionKind,
            resourceRevision,
            keyVersion,
            memberKeyGeneration,
            nonce);
    }
}

internal sealed record MemberVaultMetadataCiphertext
{
    internal VaultScope Scope { get; }
    internal MetadataRevision MetadataRevision { get; }
    internal MemberVaultMetadataHeader Header { get; }
    internal byte[] Ciphertext { get; }

    private MemberVaultMetadataCiphertext(
        VaultScope scope,
        MetadataRevision metadataRevision,
        MemberVaultMetadataHeader header,
        byte[] ciphertext)
    {
        Scope = scope;
        MetadataRevision = metadataRevision;
        Header = header;
        Ciphertext = ciphertext.ToArray();
    }

    internal static MemberVaultMetadataCiphertext Create(
        VaultScope scope,
        MetadataRevision metadataRevision,
        MemberVaultMetadataHeader header,
        byte[] ciphertext)
    {
        scope.Validate();

        if (header.ResourceRevision != metadataRevision)
        {
            throw new DomainException("Member Vault metadata revisions do not match.");
        }

        if (ciphertext.Length is < 16 or > VaultProtocol.MaximumMetadataCiphertextBytes)
        {
            throw new DomainException("Member Vault metadata ciphertext size is outside the protocol limit.");
        }

        return new MemberVaultMetadataCiphertext(scope, metadataRevision, header, ciphertext);
    }
}
