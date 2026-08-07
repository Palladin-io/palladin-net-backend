using System.Security.Cryptography;
using NodaTime;
using Palladin.Core.Types.Exceptions;

namespace Palladin.Module.Vault.Domain;

internal enum VaultKeyRotationPreparedItemKind
{
    VaultMetadata = 1,
    MemberVaultKey = 2,
    EntryKey = 3,
    EntryDiscovery = 4,
    AgentDiscoveryEnvelope = 5,
    VaultKeyMaterial = 6,
    VaultPublicTrustAnchor = 7,
}

internal sealed class VaultKeyRotationPreparedItem
{
    internal Guid OrganizationId { get; private set; }
    internal Guid VaultId { get; private set; }
    internal Guid RotationId { get; private set; }
    internal VaultKeyRotationPreparedItemKind Kind { get; private set; }
    internal Guid SubjectId { get; private set; }
    internal ulong SubjectVersion { get; private set; }
    internal ulong SourceRevision { get; private set; }
    internal byte[] Payload { get; private set; } = [];
    internal byte[] PayloadDigest { get; private set; } = [];
    internal Instant PreparedAt { get; private set; }

    private VaultKeyRotationPreparedItem() { }

    internal static VaultKeyRotationPreparedItem Create(
        VaultKeyRotation rotation,
        VaultKeyRotationPreparedItemKind kind,
        Guid subjectId,
        ulong subjectVersion,
        ulong sourceRevision,
        byte[] payload,
        Instant preparedAt)
    {
        if (subjectId == Guid.Empty || payload.Length is 0 or > VaultProtocol.MaximumRotationPreparedPayloadBytes)
        {
            throw new DomainException("Prepared Vault rotation item is invalid.");
        }

        return new VaultKeyRotationPreparedItem
        {
            OrganizationId = rotation.OrganizationId,
            VaultId = rotation.VaultId,
            RotationId = rotation.Id,
            Kind = kind,
            SubjectId = subjectId,
            SubjectVersion = subjectVersion,
            SourceRevision = sourceRevision,
            Payload = payload.ToArray(),
            PayloadDigest = SHA256.HashData(payload),
            PreparedAt = preparedAt,
        };
    }

    internal bool HasSameContent(VaultKeyRotationPreparedItem candidate) =>
        OrganizationId == candidate.OrganizationId
        && VaultId == candidate.VaultId
        && RotationId == candidate.RotationId
        && Kind == candidate.Kind
        && SubjectId == candidate.SubjectId
        && SubjectVersion == candidate.SubjectVersion
        && SourceRevision == candidate.SourceRevision
        && CryptographicOperations.FixedTimeEquals(PayloadDigest, candidate.PayloadDigest)
        && Payload.AsSpan().SequenceEqual(candidate.Payload);

    internal void ReplaceWith(VaultKeyRotationPreparedItem candidate)
    {
        if (OrganizationId != candidate.OrganizationId
            || VaultId != candidate.VaultId
            || RotationId != candidate.RotationId
            || Kind != candidate.Kind
            || SubjectId != candidate.SubjectId
            || SubjectVersion != candidate.SubjectVersion)
        {
            throw new DomainException("Prepared Vault rotation item identity cannot change.");
        }

        SourceRevision = candidate.SourceRevision;
        Payload = candidate.Payload.ToArray();
        PayloadDigest = candidate.PayloadDigest.ToArray();
        PreparedAt = candidate.PreparedAt;
    }
}
