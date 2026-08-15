using System.Globalization;
using Palladin.Core.Types.Exceptions;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Infrastructure.Crypto;

internal static class FullGrantPreparationEnvelopeValidator
{
    internal static GrantEntryScope Validate(
        GrantEntryEnvelopeContract contract,
        FullGrantPreparation preparation,
        ulong currentEntryRevision)
    {
        if (contract.OrganizationId != preparation.OrganizationId
            || contract.VaultId != preparation.VaultId
            || contract.GrantId != preparation.Id
            || contract.AgentId != preparation.AgentId
            || contract.GrantEnvelopeRevision != "1"
            || contract.EntryRevision != currentEntryRevision.ToString(CultureInfo.InvariantCulture)
            || contract.GrantKeyVersion != 1
            || contract.MemberKeyGeneration != preparation.MemberKeyGeneration
            || contract.RecipientAgentKeyVersion != preparation.RecipientAgentKeyVersion
            || contract.ExpiresAt != preparation.GrantExpiresAt
            || contract.RemainingUses != preparation.QueryLimit)
        {
            throw new DomainException("Grant envelope does not match its FULL grant preparation.");
        }

        var scope = GrantEnvelopeContractMapper.ToDomain(contract, preparation.Methods, preparation.AgentId);
        if (!scope.Envelope!.AgentKeyFingerprint.AsSpan().SequenceEqual(preparation.AgentKeyFingerprint))
        {
            throw new DomainException("Grant envelope Agent key fingerprint is invalid.");
        }

        return scope;
    }
}
