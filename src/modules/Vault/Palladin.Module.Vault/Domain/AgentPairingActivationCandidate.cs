using System.Security.Cryptography;
using Palladin.Core.Types.Exceptions;

namespace Palladin.Module.Vault.Domain;

internal sealed class AgentPairingActivationCandidate
{
    internal Guid ActivationId { get; private set; }
    internal Guid OrganizationId { get; private set; }
    internal Guid AgentId { get; private set; }
    internal Guid VaultId { get; private set; }
    internal ManifestRevision ManifestRevision { get; private set; }
    internal byte[] VaultSigningKeyFingerprint { get; private set; } = [];
    internal byte[] SignedManifestDigest { get; private set; } = [];

    private AgentPairingActivationCandidate() { }

    internal static AgentPairingActivationCandidate Create(
        Guid activationId,
        Guid organizationId,
        Guid agentId,
        Guid vaultId,
        ManifestRevision manifestRevision,
        byte[] vaultSigningKeyFingerprint,
        byte[] signedManifestDigest)
    {
        if (activationId == Guid.Empty || organizationId == Guid.Empty
            || agentId == Guid.Empty || vaultId == Guid.Empty
            || vaultSigningKeyFingerprint.Length != VaultProtocol.FingerprintBytes
            || signedManifestDigest.Length != SHA256.HashSizeInBytes)
        {
            throw new DomainException("Agent pairing candidate bindings are invalid.");
        }

        return new AgentPairingActivationCandidate
        {
            ActivationId = activationId,
            OrganizationId = organizationId,
            AgentId = agentId,
            VaultId = vaultId,
            ManifestRevision = manifestRevision,
            VaultSigningKeyFingerprint = vaultSigningKeyFingerprint.ToArray(),
            SignedManifestDigest = signedManifestDigest.ToArray(),
        };
    }
}
