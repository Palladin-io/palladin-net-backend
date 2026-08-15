using System.Security.Cryptography;
using NodaTime;
using Palladin.Core.Types.Exceptions;

namespace Palladin.Module.Vault.Domain;

internal sealed class FullGrantPreparationEntry
{
    internal Guid OrganizationId { get; private set; }
    internal Guid VaultId { get; private set; }
    internal Guid PreparationId { get; private set; }
    internal Guid EntryId { get; private set; }
    internal ulong EntryRevision { get; private set; }
    internal byte[] Payload { get; private set; } = [];
    internal byte[] PayloadDigest { get; private set; } = [];
    internal Instant PreparedAt { get; private set; }

    private FullGrantPreparationEntry() { }

    internal static FullGrantPreparationEntry Create(
        FullGrantPreparation preparation,
        GrantEntryScope scope,
        byte[] payload,
        Instant preparedAt)
    {
        if (scope.OrganizationId != preparation.OrganizationId
            || scope.VaultId != preparation.VaultId
            || scope.GrantId != preparation.Id
            || scope.Methods != preparation.Methods
            || scope.Envelope is null
            || payload.Length is 0 or > VaultProtocol.MaximumPreparedGrantEnvelopeBytes)
        {
            throw new DomainException("Prepared FULL grant entry does not match its preparation.");
        }

        return new FullGrantPreparationEntry
        {
            OrganizationId = preparation.OrganizationId,
            VaultId = preparation.VaultId,
            PreparationId = preparation.Id,
            EntryId = scope.EntryId,
            EntryRevision = scope.Envelope.EntryRevision,
            Payload = payload.ToArray(),
            PayloadDigest = SHA256.HashData(payload),
            PreparedAt = preparedAt,
        };
    }

    internal bool HasSameContent(FullGrantPreparationEntry candidate) =>
        OrganizationId == candidate.OrganizationId
        && VaultId == candidate.VaultId
        && PreparationId == candidate.PreparationId
        && EntryId == candidate.EntryId
        && EntryRevision == candidate.EntryRevision
        && CryptographicOperations.FixedTimeEquals(PayloadDigest, candidate.PayloadDigest)
        && Payload.AsSpan().SequenceEqual(candidate.Payload);
}
