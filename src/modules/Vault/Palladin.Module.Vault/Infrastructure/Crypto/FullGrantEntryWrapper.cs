using System.Diagnostics.CodeAnalysis;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Infrastructure.Crypto;

// Shared FULL-grant wrapping used by CreateEntry and ImportEntries: a newly written entry is only
// covered by the vault's active FULL grants, so the owner's client must supply fresh per-entry material
// for exactly that set. On a match the grants wrap the new entry (ciphertext only — VK is never wrapped
// for an agent); on a mismatch nothing is mutated and the caller emits the error response.
internal static class FullGrantEntryWrapper
{
    public const string MaterialMismatchMessage =
        "Grant material must be supplied for exactly the set of active FULL grants on this vault.";

    public static bool TryWrapNewEntry(
        IReadOnlyList<FullGrant> activeFullGrants,
        EntryScope entry,
        IReadOnlyList<GrantEntryEnvelopeContract> providedMaterial,
        IReadOnlyDictionary<Guid, byte[]> agentFingerprints,
        IReadOnlyDictionary<Guid, uint> recipientAgentKeyVersions,
        uint memberKeyGeneration,
        [NotNullWhen(false)] out string? error)
    {
        var providedIds = providedMaterial.Select(m => m.GrantId).ToList();
        var providedSet = providedIds.ToHashSet();
        var fullGrantSet = activeFullGrants.Select(g => g.Id).ToHashSet();

        // Exact-match enforcement: no missing, no extra/non-covering, no duplicates.
        if (providedIds.Count != providedSet.Count || !providedSet.SetEquals(fullGrantSet))
        {
            error = MaterialMismatchMessage;
            return false;
        }

        var materialByGrantId = providedMaterial.ToDictionary(m => m.GrantId);
        foreach (var grant in activeFullGrants)
        {
            var material = materialByGrantId[grant.Id];
            try
            {
                if (material.OrganizationId != entry.OrganizationId || material.VaultId != entry.VaultId
                    || material.EntryId != entry.EntryId || material.EntryRevision != "1"
                    || material.GrantEnvelopeRevision != "1" || material.GrantKeyVersion != 1
                    || material.MemberKeyGeneration != memberKeyGeneration
                    || material.RecipientAgentKeyVersion != recipientAgentKeyVersions[grant.AgentId]
                    || material.ExpiresAt != grant.ExpiresAt
                    || material.RemainingUses != (grant.QueryLimit is null ? null : grant.QueryLimit - grant.QueryCount))
                {
                    error = MaterialMismatchMessage;
                    return false;
                }

                var scope = GrantEnvelopeContractMapper.ToDomain(material, grant.Methods, grant.AgentId);
                if (!scope.Envelope!.AgentKeyFingerprint.AsSpan().SequenceEqual(agentFingerprints[grant.AgentId]))
                {
                    error = MaterialMismatchMessage;
                    return false;
                }

                grant.WrapNewEntry(scope);
            }
            catch (Exception ex) when (ex is FormatException or Palladin.Core.Types.Exceptions.DomainException or KeyNotFoundException)
            {
                error = MaterialMismatchMessage;
                return false;
            }
        }

        error = null;
        return true;
    }
}
