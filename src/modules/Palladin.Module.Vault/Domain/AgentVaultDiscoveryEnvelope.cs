using NodaTime;
using Palladin.Core.Types.Exceptions;

namespace Palladin.Module.Vault.Domain;

internal sealed record ValidatedVaultManifest(
    ushort ProtocolVersion,
    ushort AlgorithmSuite,
    VaultScope Scope,
    Guid AgentId,
    byte[] AgentX25519Fingerprint,
    byte[] AgentEd25519Fingerprint,
    byte[] VaultSigningPublicKey,
    byte[] VaultSigningKeyFingerprint,
    ManifestSigningKeyVersion ManifestSigningKeyVersion,
    byte[] VaultAgentMessagePublicKey,
    byte[] VaultAgentMessageKeyFingerprint,
    AgentMessageKeyVersion AgentMessageKeyVersion,
    VdkVersion VdkVersion,
    byte[] AgentWrappedVdkDigest,
    ManifestRevision ManifestRevision,
    Instant IssuedAt,
    ushort MinimumAgentRuntimeProtocol,
    byte[] Signature);

internal sealed record ValidatedAgentDiscoveryProvisioning(
    VaultScope Scope,
    Guid AgentId,
    ushort ProtocolVersion,
    ushort AlgorithmSuite,
    VdkVersion VdkVersion,
    AgentRecipientKeyVersion RecipientAgentKeyVersion,
    byte[] RecipientAgentKeyFingerprint,
    byte[] AgentWrappedVdk,
    ManifestRevision ManifestRevision,
    byte[] ManifestSignature,
    ValidatedVaultManifest Manifest);

internal sealed class AgentVaultDiscoveryEnvelope
{
    public Guid OrganizationId { get; private set; }
    public Guid VaultId { get; private set; }
    public Guid AgentId { get; private set; }
    internal ushort ProtocolVersion { get; private set; }
    internal string CryptoSuiteId { get; private set; } = string.Empty;
    internal string WrapperSuiteId { get; private set; } = string.Empty;
    internal VdkVersion VdkVersion { get; private set; }
    internal AgentRecipientKeyVersion RecipientAgentKeyVersion { get; private set; }
    internal byte[] RecipientAgentKeyFingerprint { get; private set; } = [];
    internal byte[] AgentWrappedVdk { get; private set; } = [];
    internal ManifestRevision ManifestRevision { get; private set; }
    internal byte[] ManifestSignature { get; private set; } = [];
    internal byte[] AgentX25519Fingerprint { get; private set; } = [];
    internal byte[] AgentEd25519Fingerprint { get; private set; } = [];
    internal byte[] VaultSigningPublicKey { get; private set; } = [];
    internal byte[] VaultSigningKeyFingerprint { get; private set; } = [];
    internal ManifestSigningKeyVersion ManifestSigningKeyVersion { get; private set; }
    internal byte[] VaultAgentMessagePublicKey { get; private set; } = [];
    internal byte[] VaultAgentMessageKeyFingerprint { get; private set; } = [];
    internal AgentMessageKeyVersion AgentMessageKeyVersion { get; private set; }
    internal byte[] AgentWrappedVdkDigest { get; private set; } = [];
    internal Instant IssuedAt { get; private set; }
    internal ushort MinimumAgentRuntimeProtocol { get; private set; }
    internal Guid ProvisionedBy { get; private set; }
    internal Instant ProvisionedAt { get; private set; }
    internal uint ProvisionedAccessEpoch { get; private set; }
    internal Instant? RevokedAt { get; private set; }

    private AgentVaultDiscoveryEnvelope() { }

    internal static AgentVaultDiscoveryEnvelope Create(
        ValidatedAgentDiscoveryProvisioning provisioning,
        Guid provisionedBy,
        uint provisionedAccessEpoch,
        Instant provisionedAt)
    {
        ValidateShape(provisioning);
        var envelope = new AgentVaultDiscoveryEnvelope
        {
            OrganizationId = provisioning.Scope.OrganizationId,
            VaultId = provisioning.Scope.VaultId,
            AgentId = provisioning.AgentId,
        };
        envelope.Apply(provisioning, provisionedBy, provisionedAccessEpoch, provisionedAt);
        return envelope;
    }

    internal bool Replace(
        ValidatedAgentDiscoveryProvisioning provisioning,
        Guid provisionedBy,
        uint provisionedAccessEpoch,
        Instant provisionedAt)
    {
        ValidateShape(provisioning);
        if (provisioning.Scope != new VaultScope(OrganizationId, VaultId) || provisioning.AgentId != AgentId)
        {
            throw new DomainException("Agent Discovery provisioning scope cannot change.");
        }

        if (provisioning.ManifestRevision.Value < ManifestRevision.Value)
        {
            throw new DomainException("Vault manifest revision cannot move backwards.");
        }

        if (provisioning.ManifestRevision == ManifestRevision)
        {
            if (!HasSameContent(provisioning))
            {
                throw new DomainException("A Vault manifest revision cannot be reused with different content.");
            }

            return false;
        }

        Apply(provisioning, provisionedBy, provisionedAccessEpoch, provisionedAt);
        return true;
    }

    internal bool RevokeForAcceptedAgentDeactivation(
        Instant revokedAt,
        uint deactivatedAccessEpoch)
    {
        if (RevokedAt is not null
            || ProvisionedAccessEpoch > deactivatedAccessEpoch)
        {
            return false;
        }

        RevokedAt = revokedAt;
        return true;
    }

    private void Apply(
        ValidatedAgentDiscoveryProvisioning provisioning,
        Guid provisionedBy,
        uint provisionedAccessEpoch,
        Instant provisionedAt)
    {
        if (provisionedAccessEpoch == 0)
        {
            throw new DomainException("Agent Discovery provisioning requires a positive access epoch.");
        }

        ProtocolVersion = provisioning.ProtocolVersion;
        CryptoSuiteId = Domain.CryptoSuiteId.XChaCha20Poly1305V1;
        WrapperSuiteId = Infrastructure.Crypto.X25519SealedBoxContract.SuiteId;
        VdkVersion = provisioning.VdkVersion;
        RecipientAgentKeyVersion = provisioning.RecipientAgentKeyVersion;
        RecipientAgentKeyFingerprint = provisioning.RecipientAgentKeyFingerprint.ToArray();
        AgentWrappedVdk = provisioning.AgentWrappedVdk.ToArray();
        ManifestRevision = provisioning.ManifestRevision;
        ManifestSignature = provisioning.ManifestSignature.ToArray();
        AgentX25519Fingerprint = provisioning.Manifest.AgentX25519Fingerprint.ToArray();
        AgentEd25519Fingerprint = provisioning.Manifest.AgentEd25519Fingerprint.ToArray();
        VaultSigningPublicKey = provisioning.Manifest.VaultSigningPublicKey.ToArray();
        VaultSigningKeyFingerprint = provisioning.Manifest.VaultSigningKeyFingerprint.ToArray();
        ManifestSigningKeyVersion = provisioning.Manifest.ManifestSigningKeyVersion;
        VaultAgentMessagePublicKey = provisioning.Manifest.VaultAgentMessagePublicKey.ToArray();
        VaultAgentMessageKeyFingerprint = provisioning.Manifest.VaultAgentMessageKeyFingerprint.ToArray();
        AgentMessageKeyVersion = provisioning.Manifest.AgentMessageKeyVersion;
        AgentWrappedVdkDigest = provisioning.Manifest.AgentWrappedVdkDigest.ToArray();
        IssuedAt = provisioning.Manifest.IssuedAt;
        MinimumAgentRuntimeProtocol = provisioning.Manifest.MinimumAgentRuntimeProtocol;
        ProvisionedBy = provisionedBy;
        ProvisionedAt = provisionedAt;
        ProvisionedAccessEpoch = provisionedAccessEpoch;
        RevokedAt = null;
    }

    private bool HasSameContent(ValidatedAgentDiscoveryProvisioning candidate) =>
        ProtocolVersion == candidate.ProtocolVersion
        && CryptoSuiteId == Domain.CryptoSuiteId.XChaCha20Poly1305V1
        && WrapperSuiteId == Infrastructure.Crypto.X25519SealedBoxContract.SuiteId
        && VdkVersion == candidate.VdkVersion
        && RecipientAgentKeyVersion == candidate.RecipientAgentKeyVersion
        && RecipientAgentKeyFingerprint.AsSpan().SequenceEqual(candidate.RecipientAgentKeyFingerprint)
        && AgentWrappedVdk.AsSpan().SequenceEqual(candidate.AgentWrappedVdk)
        && ManifestSignature.AsSpan().SequenceEqual(candidate.ManifestSignature)
        && AgentX25519Fingerprint.AsSpan().SequenceEqual(candidate.Manifest.AgentX25519Fingerprint)
        && AgentEd25519Fingerprint.AsSpan().SequenceEqual(candidate.Manifest.AgentEd25519Fingerprint)
        && VaultSigningPublicKey.AsSpan().SequenceEqual(candidate.Manifest.VaultSigningPublicKey)
        && VaultSigningKeyFingerprint.AsSpan().SequenceEqual(candidate.Manifest.VaultSigningKeyFingerprint)
        && ManifestSigningKeyVersion == candidate.Manifest.ManifestSigningKeyVersion
        && VaultAgentMessagePublicKey.AsSpan().SequenceEqual(candidate.Manifest.VaultAgentMessagePublicKey)
        && VaultAgentMessageKeyFingerprint.AsSpan().SequenceEqual(candidate.Manifest.VaultAgentMessageKeyFingerprint)
        && AgentMessageKeyVersion == candidate.Manifest.AgentMessageKeyVersion
        && AgentWrappedVdkDigest.AsSpan().SequenceEqual(candidate.Manifest.AgentWrappedVdkDigest)
        && IssuedAt == candidate.Manifest.IssuedAt
        && MinimumAgentRuntimeProtocol == candidate.Manifest.MinimumAgentRuntimeProtocol;

    private static void ValidateShape(ValidatedAgentDiscoveryProvisioning provisioning)
    {
        provisioning.Scope.Validate();
        if (provisioning.AgentId == Guid.Empty
            || provisioning.ProtocolVersion != VaultProtocol.CurrentVersion
            || provisioning.AlgorithmSuite != VaultProtocol.AlgorithmSuite
            || provisioning.RecipientAgentKeyFingerprint.Length != VaultProtocol.FingerprintBytes
            || provisioning.AgentWrappedVdk.Length != Infrastructure.Crypto.X25519SealedBoxContract.EncodedPackageBytes
            || provisioning.ManifestSignature.Length != 64
            || provisioning.Manifest.Scope != provisioning.Scope
            || provisioning.Manifest.AgentId != provisioning.AgentId
            || provisioning.Manifest.ProtocolVersion != provisioning.ProtocolVersion
            || provisioning.Manifest.AlgorithmSuite != provisioning.AlgorithmSuite
            || provisioning.Manifest.VdkVersion != provisioning.VdkVersion
            || provisioning.Manifest.ManifestRevision != provisioning.ManifestRevision
            || !provisioning.Manifest.Signature.AsSpan().SequenceEqual(provisioning.ManifestSignature))
        {
            throw new DomainException("Agent Discovery provisioning does not match the frozen protocol contract.");
        }
    }
}
