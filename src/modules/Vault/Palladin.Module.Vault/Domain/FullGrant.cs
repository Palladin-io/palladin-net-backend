using Palladin.Core.Types;
using NodaTime;

namespace Palladin.Module.Vault.Domain;

internal sealed class FullGrant : Grant
{
    public override GrantType Type => GrantType.Full;

    // FULL grants cover entries through durable per-entry scopes backed by revision-bound
    // envelopes. Deleting an envelope removes active coverage without deleting the durable scope.
    // New entries are attached by the owner's client during entry creation because the server never
    // has the vault key required to construct the agent envelope.
    public override bool Covers(Guid entryId) => GrantEntryScopes.Any(x => x.EntryId == entryId && x.Envelope != null);

    private FullGrant() { }

    internal static FullGrant CreateProactively(
        Guid id,
        Guid vaultId,
        Guid organizationId,
        Guid agentId,
        string agentPublicKey,
        IReadOnlyCollection<GrantEntryScope> scopes,
        Instant? expiresAt,
        int? queryLimit,
        string expirySource,
        GrantMethods methods,
        Guid createdBy,
        GrantNames names,
        Instant now,
        uint agentAccessEpoch)
    {
        ArgumentOutOfRangeException.ThrowIfZero(agentAccessEpoch);
        if (scopes.Count == 0 || scopes.Any(scope => scope.OrganizationId != organizationId
                                                    || scope.VaultId != vaultId
                                                    || scope.GrantId != id
                                                    || scope.Methods != methods))
        {
            throw new Palladin.Core.Types.Exceptions.DomainException("Grant scopes do not match the grant.");
        }
        var grant = new FullGrant
        {
            Id = id,
            VaultId = vaultId,
            OrganizationId = organizationId,
            AgentId = agentId,
            AgentAccessEpoch = agentAccessEpoch,
            AgentPublicKey = agentPublicKey,
            Status = GrantStatus.Active,
            ExpiresAt = expiresAt,
            QueryLimit = queryLimit,
            QueryCount = 0,
            ExpirySource = expirySource,
            Methods = methods,
            CreatedAt = now,
            CreatedBy = createdBy,
            UpdatedAt = now,
            GrantEntryScopes = [.. scopes],
        };

        grant.EmitCreated(names);

        return grant;
    }
}
