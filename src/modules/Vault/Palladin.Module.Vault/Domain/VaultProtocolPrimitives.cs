using Palladin.Core.Types.Exceptions;

namespace Palladin.Module.Vault.Domain;

internal static class VaultProtocol
{
    internal const ushort CurrentVersion = 2;
    internal const ushort AlgorithmSuite = 1;
    internal const ushort VaultResourceKind = 1;
    internal const ushort MemberVaultMetadataProjectionKind = 1;
    internal const ushort EntryResourceKind = 2;
    internal const ushort GrantRequestResourceKind = 3;
    internal const ushort MemberIndexProjectionKind = 2;
    internal const ushort MemberSecretProjectionKind = 3;
    internal const ushort AgentDiscoveryProjectionKind = 4;
    internal const ushort EncryptedReasonProjectionKind = 5;
    internal const ushort WrappedKeyProjectionKind = 8;
    internal const ushort VaultPrivateKeyProjectionKind = 7;
    internal const ushort VaultDiscoveryKeyProjectionKind = 12;
    internal const int NonceBytes = 24;
    internal const int FingerprintBytes = 32;
    internal const int MaximumMetadataCiphertextBytes = 16_384;
    internal const int MaximumSealedVaultKeyBytes = 256;
    internal const int MaximumSealedVdkBytes = 512;
    internal const int MaximumSealedVdkBase64UrlLength = 683;
    internal const int MaximumMemberIndexCiphertextBytes = 32_768;
    internal const int MaximumMemberSecretCiphertextBytes = 262_144;
    internal const int MaximumAgentDiscoveryCiphertextBytes = 16_384;
    internal const int MaximumWrappedEntryKeyBytes = 64;
    internal const int MaximumVaultKeyMaterialCiphertextBytes = 256;
    internal const int RotationLeaseSeconds = 120;
    internal const int MaximumRotationBatchItems = 100;
    internal const int MaximumRotationPreparedPayloadBytes = 32 * 1024;
    internal const int MaximumRotationPreparedEntryHeadPayloadBytes = 512 * 1024;
    internal const int MaximumPreparedGrantEnvelopeBytes = 384 * 1024;
}

internal enum VaultKeyMaterialKind
{
    DiscoveryKey = 1,
    AgentMessagePrivateKey = 2,
    ManifestSigningPrivateKey = 3,
}

internal readonly record struct VaultScope(Guid OrganizationId, Guid VaultId)
{
    internal void Validate()
    {
        if (OrganizationId == Guid.Empty || VaultId == Guid.Empty)
        {
            throw new DomainException("Vault scope must contain organization and vault identifiers.");
        }
    }
}

internal readonly record struct EntryScope(Guid OrganizationId, Guid VaultId, Guid EntryId)
{
    internal VaultScope VaultScope => new(OrganizationId, VaultId);

    internal void Validate()
    {
        VaultScope.Validate();
        if (EntryId == Guid.Empty)
        {
            throw new DomainException("Entry scope must contain an entry identifier.");
        }
    }
}

internal readonly record struct MetadataRevision
{
    internal ulong Value { get; }

    internal MetadataRevision(ulong value)
    {
        if (value == 0)
        {
            throw new DomainException("Metadata revision must be greater than zero.");
        }

        Value = value;
    }
}

internal readonly record struct MemberSequence
{
    internal ulong Value { get; }

    // Zero is the valid cursor of a newly created Vault. Persisted Entry versions validate that
    // their allocated sequence is greater than zero at the aggregate boundary.
    internal MemberSequence(ulong value) => Value = value;
}

internal readonly record struct DiscoverySequence
{
    internal ulong Value { get; }

    // Zero is the valid cursor/floor before the first discovery projection is allocated.
    internal DiscoverySequence(ulong value) => Value = value;
}

internal readonly record struct EntryRevision
{
    internal ulong Value { get; }

    internal EntryRevision(ulong value)
    {
        if (value == 0)
        {
            throw new DomainException("Entry revision must be greater than zero.");
        }

        Value = value;
    }
}

internal readonly record struct MemberIndexRevision
{
    internal ulong Value { get; }

    internal MemberIndexRevision(ulong value)
    {
        if (value == 0)
        {
            throw new DomainException("Member index revision must be greater than zero.");
        }

        Value = value;
    }
}

internal readonly record struct AgentDiscoveryRevision
{
    internal ulong Value { get; }

    internal AgentDiscoveryRevision(ulong value)
    {
        if (value == 0)
        {
            throw new DomainException("Agent Discovery revision must be greater than zero.");
        }

        Value = value;
    }
}

internal readonly record struct AgentDiscoveryRevisionWatermark(ulong Value);

internal readonly record struct EntryKeyVersion
{
    internal uint Value { get; }

    internal EntryKeyVersion(uint value)
    {
        if (value == 0)
        {
            throw new DomainException("Entry key version must be greater than zero.");
        }

        Value = value;
    }
}

internal readonly record struct EntryKeyWrapperRevision
{
    internal ulong Value { get; }

    internal EntryKeyWrapperRevision(ulong value)
    {
        if (value == 0)
        {
            throw new DomainException("Entry key wrapper revision must be greater than zero.");
        }

        Value = value;
    }
}

internal readonly record struct VaultKeyVersion
{
    internal uint Value { get; }

    internal VaultKeyVersion(uint value)
    {
        if (value == 0)
        {
            throw new DomainException("Vault key version must be greater than zero.");
        }

        Value = value;
    }
}

internal readonly record struct VdkVersion
{
    internal uint Value { get; }

    internal VdkVersion(uint value)
    {
        if (value == 0)
        {
            throw new DomainException("Vault discovery key version must be greater than zero.");
        }

        Value = value;
    }
}

internal readonly record struct AgentMessageKeyVersion
{
    internal uint Value { get; }

    internal AgentMessageKeyVersion(uint value)
    {
        if (value == 0)
        {
            throw new DomainException("Agent message key version must be greater than zero.");
        }

        Value = value;
    }
}

internal readonly record struct ManifestSigningKeyVersion
{
    internal uint Value { get; }

    internal ManifestSigningKeyVersion(uint value)
    {
        if (value == 0)
        {
            throw new DomainException("Manifest signing key version must be greater than zero.");
        }

        Value = value;
    }
}

internal readonly record struct MemberKeyGeneration
{
    internal uint Value { get; }

    internal MemberKeyGeneration(uint value)
    {
        if (value == 0)
        {
            throw new DomainException("Member key generation must be greater than zero.");
        }

        Value = value;
    }
}

internal readonly record struct MemberRecipientKeyVersion
{
    internal uint Value { get; }

    internal MemberRecipientKeyVersion(uint value)
    {
        if (value == 0)
        {
            throw new DomainException("Member recipient key version must be greater than zero.");
        }

        Value = value;
    }
}

internal readonly record struct AgentRecipientKeyVersion
{
    internal uint Value { get; }

    internal AgentRecipientKeyVersion(uint value)
    {
        if (value == 0)
        {
            throw new DomainException("Agent recipient key version must be greater than zero.");
        }

        Value = value;
    }
}

internal readonly record struct ManifestRevision
{
    internal ulong Value { get; }

    internal ManifestRevision(ulong value)
    {
        if (value == 0)
        {
            throw new DomainException("Manifest revision must be greater than zero.");
        }

        Value = value;
    }
}

internal sealed record VaultKeyEpoch(
    VaultKeyVersion VaultKeyVersion,
    VdkVersion VdkVersion,
    AgentMessageKeyVersion AgentMessageKeyVersion,
    ManifestSigningKeyVersion ManifestSigningKeyVersion);

internal sealed record AllocatedVaultSequences(
    MemberSequence MemberSequence,
    DiscoverySequence? DiscoverySequence);
