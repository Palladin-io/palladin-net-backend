using Palladin.Core.Types;
using NodaTime;

namespace Palladin.Module.Vault.Domain;

internal sealed class FullGrant : Grant
{
    public override GrantType Type => GrantType.Full;

    // FULL is a temporary Vault crypto membership: one Agent-wrapped current VK covers every active
    // Entry in the Vault. The backend never sees the unwrapped VK.
    public override bool Covers(Guid entryId) => AgentWrappedVaultKey is not null;

    private FullGrant() { }

    internal static FullGrant CreateProactively(
        Guid id,
        Guid vaultId,
        Guid organizationId,
        Guid agentId,
        string agentPublicKey,
        AgentWrappedVaultKey agentWrappedVaultKey,
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
        if (agentWrappedVaultKey.OrganizationId != organizationId
            || agentWrappedVaultKey.VaultId != vaultId
            || agentWrappedVaultKey.GrantId != id
            || agentWrappedVaultKey.AgentId != agentId
            || agentWrappedVaultKey.AgentAccessEpoch != agentAccessEpoch)
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
            AgentWrappedVaultKey = agentWrappedVaultKey,
        };

        grant.EmitCreated(names);

        return grant;
    }
}
