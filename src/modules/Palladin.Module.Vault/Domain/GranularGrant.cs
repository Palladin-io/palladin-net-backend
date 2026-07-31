using Palladin.Core.Types;
using NodaTime;

namespace Palladin.Module.Vault.Domain;

internal sealed class GranularGrant : Grant
{
    public Guid EntryId { get; private set; }

    public override GrantType Type => GrantType.Granular;

    public override bool Covers(Guid entryId) => entryId == EntryId;

    private GranularGrant() { }

    internal static GranularGrant CreateProactively(
        Guid id,
        Guid vaultId,
        Guid organizationId,
        Guid agentId,
        string agentPublicKey,
        Guid entryId,
        GrantEntryScope scope,
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
        if (scope.OrganizationId != organizationId || scope.VaultId != vaultId
            || scope.GrantId != id || scope.EntryId != entryId || scope.Methods != methods)
        {
            throw new Palladin.Core.Types.Exceptions.DomainException("Grant scope does not match the grant.");
        }
        var grant = new GranularGrant
        {
            Id = id,
            VaultId = vaultId,
            OrganizationId = organizationId,
            AgentId = agentId,
            AgentAccessEpoch = agentAccessEpoch,
            AgentPublicKey = agentPublicKey,
            EntryId = entryId,
            Status = GrantStatus.Active,
            ExpiresAt = expiresAt,
            QueryLimit = queryLimit,
            QueryCount = 0,
            ExpirySource = expirySource,
            Methods = methods,
            CreatedAt = now,
            CreatedBy = createdBy,
            UpdatedAt = now,
            GrantEntryScopes =
            [
                scope,
            ],
        };

        grant.EmitCreated(names);

        return grant;
    }

    // Agent-initiated access request: a single-entry grant awaiting user approval. No crypto material
    // yet — the server cannot wrap a DEK; the approving user's client supplies it on Approve.
    // CreatedBy is null (the actor is the agent, identified by AgentId).
    internal static GranularGrant RequestAccess(
        Guid id,
        Guid vaultId,
        Guid organizationId,
        Guid agentId,
        string agentPublicKey,
        Guid entryId,
        GrantNames names,
        EncryptedReasonEnvelope encryptedReason,
        GrantMethods requestedMethods,
        Instant now,
        uint agentAccessEpoch)
    {
        ArgumentOutOfRangeException.ThrowIfZero(agentAccessEpoch);
        var grant = new GranularGrant
        {
            Id = id,
            VaultId = vaultId,
            OrganizationId = organizationId,
            AgentId = agentId,
            AgentAccessEpoch = agentAccessEpoch,
            AgentPublicKey = agentPublicKey,
            EntryId = entryId,
            Status = GrantStatus.Pending,
            RequestType = GrantRequestType.AccessRequest,
            EncryptedReason = encryptedReason,
            Methods = requestedMethods,
            QueryCount = 0,
            CreatedAt = now,
            CreatedBy = null,
            UpdatedAt = now,
        };

        grant.EmitRequested(entryId, names);

        return grant;
    }
}
