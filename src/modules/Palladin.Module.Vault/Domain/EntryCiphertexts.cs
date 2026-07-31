using Palladin.Core.Types;
using Palladin.Core.Types.Exceptions;

namespace Palladin.Module.Vault.Domain;

internal sealed record EntryEnvelopeHeader
{
    internal ushort ProtocolVersion { get; }
    internal ushort AlgorithmSuite { get; }
    internal ushort ResourceKind { get; }
    internal ushort ProjectionKind { get; }
    internal ulong ResourceRevision { get; }
    internal uint KeyVersion { get; }
    internal MemberKeyGeneration MemberKeyGeneration { get; }
    internal byte[] Nonce { get; }

    private EntryEnvelopeHeader(
        ushort protocolVersion,
        ushort algorithmSuite,
        ushort resourceKind,
        ushort projectionKind,
        ulong resourceRevision,
        uint keyVersion,
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

    internal static EntryEnvelopeHeader Create(
        ushort protocolVersion,
        ushort algorithmSuite,
        ushort resourceKind,
        ushort projectionKind,
        ulong resourceRevision,
        uint keyVersion,
        MemberKeyGeneration memberKeyGeneration,
        byte[] nonce,
        ushort expectedProjectionKind)
    {
        if (protocolVersion != VaultProtocol.CurrentVersion
            || algorithmSuite != VaultProtocol.AlgorithmSuite
            || resourceKind != VaultProtocol.EntryResourceKind
            || projectionKind != expectedProjectionKind)
        {
            throw new DomainException("Entry envelope header is incompatible with Vault protocol 2.");
        }

        if (resourceRevision == 0 || keyVersion == 0)
        {
            throw new DomainException("Entry envelope revisions and key versions must be greater than zero.");
        }

        if (nonce.Length != VaultProtocol.NonceBytes)
        {
            throw new DomainException("Entry envelope nonce must contain exactly 24 bytes.");
        }

        return new EntryEnvelopeHeader(
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

internal sealed record MemberIndexCiphertext
{
    internal EntryScope Scope { get; }
    internal MemberIndexRevision Revision { get; }
    internal EntryEnvelopeHeader Header { get; }
    internal byte[] Ciphertext { get; }

    private MemberIndexCiphertext(
        EntryScope scope,
        MemberIndexRevision revision,
        EntryEnvelopeHeader header,
        byte[] ciphertext)
    {
        Scope = scope;
        Revision = revision;
        Header = header;
        Ciphertext = ciphertext.ToArray();
    }

    internal static MemberIndexCiphertext Create(
        EntryScope scope,
        MemberIndexRevision revision,
        EntryEnvelopeHeader header,
        byte[] ciphertext)
    {
        scope.Validate();
        ValidateHeader(header, revision.Value, VaultProtocol.MemberIndexProjectionKind);
        ValidateCiphertext(ciphertext, VaultProtocol.MaximumMemberIndexCiphertextBytes, "Member index");
        return new MemberIndexCiphertext(scope, revision, header, ciphertext);
    }

    internal bool HasSameContent(MemberIndexCiphertext other) =>
        Scope == other.Scope
        && Revision == other.Revision
        && EntryCiphertextEquality.HeaderEquals(Header, other.Header)
        && Ciphertext.AsSpan().SequenceEqual(other.Ciphertext);

    private static void ValidateHeader(EntryEnvelopeHeader header, ulong revision, ushort projectionKind)
    {
        if (header.ResourceRevision != revision || header.ProjectionKind != projectionKind)
        {
            throw new DomainException("Member index envelope is bound to a different revision or projection.");
        }
    }

    private static void ValidateCiphertext(byte[] ciphertext, int maximumBytes, string name)
    {
        if (ciphertext.Length is < 16 || ciphertext.Length > maximumBytes)
        {
            throw new DomainException($"{name} ciphertext size is outside the protocol limit.");
        }
    }
}

internal sealed record MemberSecretCiphertext
{
    internal EntryScope Scope { get; }
    internal EntryRevision Revision { get; }
    internal EntryOperation Operation { get; }
    internal EntryEnvelopeHeader Header { get; }
    internal byte[] Ciphertext { get; }

    private MemberSecretCiphertext(
        EntryScope scope,
        EntryRevision revision,
        EntryOperation operation,
        EntryEnvelopeHeader header,
        byte[] ciphertext)
    {
        Scope = scope;
        Revision = revision;
        Operation = operation;
        Header = header;
        Ciphertext = ciphertext.ToArray();
    }

    internal static MemberSecretCiphertext Create(
        EntryScope scope,
        EntryRevision revision,
        EntryOperation operation,
        EntryEnvelopeHeader header,
        byte[] ciphertext)
    {
        scope.Validate();
        if (!Enum.IsDefined(operation))
        {
            throw new DomainException("Entry operation is not registered by the canonical protocol.");
        }

        if (header.ResourceRevision != revision.Value
            || header.ProjectionKind != VaultProtocol.MemberSecretProjectionKind)
        {
            throw new DomainException("Member secret envelope is bound to a different revision or projection.");
        }

        if (ciphertext.Length is < 16 or > VaultProtocol.MaximumMemberSecretCiphertextBytes)
        {
            throw new DomainException("Member secret ciphertext size is outside the protocol limit.");
        }

        return new MemberSecretCiphertext(scope, revision, operation, header, ciphertext);
    }

    internal bool HasSameContent(MemberSecretCiphertext other) =>
        Scope == other.Scope
        && Revision == other.Revision
        && Operation == other.Operation
        && EntryCiphertextEquality.HeaderEquals(Header, other.Header)
        && Ciphertext.AsSpan().SequenceEqual(other.Ciphertext);
}

internal sealed record AgentDiscoveryCiphertext
{
    internal EntryScope Scope { get; }
    internal AgentDiscoveryRevision Revision { get; }
    internal VdkVersion VdkVersion { get; }
    internal EntryEnvelopeHeader Header { get; }
    internal byte[] Ciphertext { get; }

    private AgentDiscoveryCiphertext(
        EntryScope scope,
        AgentDiscoveryRevision revision,
        VdkVersion vdkVersion,
        EntryEnvelopeHeader header,
        byte[] ciphertext)
    {
        Scope = scope;
        Revision = revision;
        VdkVersion = vdkVersion;
        Header = header;
        Ciphertext = ciphertext.ToArray();
    }

    internal static AgentDiscoveryCiphertext Create(
        EntryScope scope,
        AgentDiscoveryRevision revision,
        VdkVersion vdkVersion,
        EntryEnvelopeHeader header,
        byte[] ciphertext)
    {
        scope.Validate();
        if (header.ResourceRevision != revision.Value
            || header.KeyVersion != vdkVersion.Value
            || header.ProjectionKind != VaultProtocol.AgentDiscoveryProjectionKind)
        {
            throw new DomainException("Agent Discovery envelope is bound to a different revision, key, or projection.");
        }

        if (ciphertext.Length is < 16 or > VaultProtocol.MaximumAgentDiscoveryCiphertextBytes)
        {
            throw new DomainException("Agent Discovery ciphertext size is outside the protocol limit.");
        }

        return new AgentDiscoveryCiphertext(scope, revision, vdkVersion, header, ciphertext);
    }

    internal bool HasSameContent(AgentDiscoveryCiphertext other) =>
        Scope == other.Scope
        && Revision == other.Revision
        && VdkVersion == other.VdkVersion
        && EntryCiphertextEquality.HeaderEquals(Header, other.Header)
        && Ciphertext.AsSpan().SequenceEqual(other.Ciphertext);
}

internal static class EntryCiphertextEquality
{
    internal static bool HeaderEquals(EntryEnvelopeHeader left, EntryEnvelopeHeader right) =>
        left.ProtocolVersion == right.ProtocolVersion
        && left.AlgorithmSuite == right.AlgorithmSuite
        && left.ResourceKind == right.ResourceKind
        && left.ProjectionKind == right.ProjectionKind
        && left.ResourceRevision == right.ResourceRevision
        && left.KeyVersion == right.KeyVersion
        && left.MemberKeyGeneration == right.MemberKeyGeneration
        && left.Nonce.AsSpan().SequenceEqual(right.Nonce);
}
