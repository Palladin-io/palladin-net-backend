using System.Security.Cryptography;
using NodaTime;
using Palladin.Core.Types.Exceptions;

namespace Palladin.Module.Vault.Domain;

internal sealed class AgentPairingActivation
{
    internal Guid Id { get; private set; }
    internal Guid OrganizationId { get; private set; }
    internal Guid AgentId { get; private set; }
    internal uint AgentAccessEpoch { get; private set; }
    internal byte[] AgentX25519Fingerprint { get; private set; } = [];
    internal byte[] AgentEd25519Fingerprint { get; private set; } = [];
    internal byte[] CandidateDigest { get; private set; } = [];
    internal int CandidateVaultCount { get; private set; }
    internal Instant CreatedAt { get; private set; }
    internal Instant ExpiresAt { get; private set; }
    internal Instant? ConfirmedAt { get; private set; }
    internal Guid? ConfirmedBy { get; private set; }

    private AgentPairingActivation() { }

    internal static AgentPairingActivation Create(
        Guid id,
        Guid organizationId,
        Guid agentId,
        uint agentAccessEpoch,
        byte[] agentX25519Fingerprint,
        byte[] agentEd25519Fingerprint,
        byte[] candidateDigest,
        int candidateVaultCount,
        Instant now,
        Duration lifetime)
    {
        if (id == Guid.Empty || organizationId == Guid.Empty || agentId == Guid.Empty
            || agentAccessEpoch == 0 || lifetime <= Duration.Zero
            || agentX25519Fingerprint.Length != VaultProtocol.FingerprintBytes
            || agentEd25519Fingerprint.Length != VaultProtocol.FingerprintBytes
            || candidateDigest.Length != SHA256.HashSizeInBytes
            || candidateVaultCount < 0)
        {
            throw new DomainException("Agent pairing activation bindings are invalid.");
        }

        return new AgentPairingActivation
        {
            Id = id,
            OrganizationId = organizationId,
            AgentId = agentId,
            AgentAccessEpoch = agentAccessEpoch,
            AgentX25519Fingerprint = agentX25519Fingerprint.ToArray(),
            AgentEd25519Fingerprint = agentEd25519Fingerprint.ToArray(),
            CandidateDigest = candidateDigest.ToArray(),
            CandidateVaultCount = candidateVaultCount,
            CreatedAt = now,
            ExpiresAt = now + lifetime,
        };
    }

    internal void Confirm(Guid confirmedBy, ReadOnlySpan<byte> digest, Instant now)
    {
        if (confirmedBy == Guid.Empty)
        {
            throw new DomainException("Agent pairing confirmation requires an authenticated Member.");
        }

        if (digest.Length != SHA256.HashSizeInBytes
            || !CryptographicOperations.FixedTimeEquals(CandidateDigest, digest))
        {
            throw new DomainException("Agent pairing transcript digest does not match the current candidate set.");
        }

        if (ConfirmedAt is not null)
        {
            if (ConfirmedBy != confirmedBy)
            {
                throw new DomainException("Agent pairing activation was already confirmed by another Member.");
            }

            return;
        }

        if (now >= ExpiresAt)
        {
            throw new DomainException("Agent pairing activation has expired.");
        }

        ConfirmedBy = confirmedBy;
        ConfirmedAt = now;
    }

}
