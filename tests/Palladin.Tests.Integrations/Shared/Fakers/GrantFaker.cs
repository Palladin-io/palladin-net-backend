using Palladin.Core.Types;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using NodaTime;

namespace Palladin.Tests.Integrations.Shared.Fakers;

internal static class GrantFaker
{
    public static PrivateCtorFaker<GranularGrant> CreateGranular(
        Guid? id = null,
        Guid? vaultId = null,
        Guid? organizationId = null,
        Guid? agentId = null,
        Guid? entryId = null,
        uint agentAccessEpoch = 1,
        GrantStatus status = GrantStatus.Active,
        Guid? createdBy = null,
        Instant? createdAt = null)
    {
        var now = PostgreSqlInstant.Normalize(createdAt ?? SystemClock.Instance.GetCurrentInstant());
        return (PrivateCtorFaker<GranularGrant>)new PrivateCtorFaker<GranularGrant>()
            .RuleFor(x => x.Id, id ?? Guid.NewGuid())
            .RuleFor(x => x.VaultId, vaultId ?? Guid.NewGuid())
            .RuleFor(x => x.OrganizationId, organizationId ?? Guid.NewGuid())
            .RuleFor(x => x.AgentId, agentId ?? Guid.NewGuid())
            .RuleFor(x => x.AgentAccessEpoch, agentAccessEpoch)
            .RuleFor(x => x.AgentPublicKey, AgentFaker.GeneratePublicKey())
            .RuleFor(x => x.EntryId, entryId ?? Guid.NewGuid())
            .RuleFor(x => x.Status, status)
            .RuleFor(x => x.Methods, GrantMethodsExtensions.All)
            .RuleFor(x => x.ExpiresAt, now.Plus(Duration.FromHours(1)))
            .RuleFor(x => x.ExpirySource, "time")
            .RuleFor(x => x.QueryCount, 0)
            .RuleFor(x => x.CreatedAt, now)
            .RuleFor(x => x.CreatedBy, createdBy ?? Guid.NewGuid())
            .RuleFor(x => x.UpdatedAt, now);
    }

    public static PrivateCtorFaker<FullGrant> CreateFull(
        Guid? id = null,
        Guid? vaultId = null,
        Guid? organizationId = null,
        Guid? agentId = null,
        uint agentAccessEpoch = 1,
        GrantStatus status = GrantStatus.Active,
        Guid? createdBy = null,
        Instant? createdAt = null)
    {
        var now = PostgreSqlInstant.Normalize(createdAt ?? SystemClock.Instance.GetCurrentInstant());
        var resolvedId = id ?? Guid.NewGuid();
        var resolvedVaultId = vaultId ?? Guid.NewGuid();
        var resolvedOrganizationId = organizationId ?? Guid.NewGuid();
        var resolvedAgentId = agentId ?? Guid.NewGuid();
        return (PrivateCtorFaker<FullGrant>)new PrivateCtorFaker<FullGrant>()
            .RuleFor(x => x.Id, resolvedId)
            .RuleFor(x => x.VaultId, resolvedVaultId)
            .RuleFor(x => x.OrganizationId, resolvedOrganizationId)
            .RuleFor(x => x.AgentId, resolvedAgentId)
            .RuleFor(x => x.AgentAccessEpoch, agentAccessEpoch)
            .RuleFor(x => x.AgentPublicKey, AgentFaker.GeneratePublicKey())
            .RuleFor(x => x.Status, status)
            .RuleFor(x => x.Methods, GrantMethodsExtensions.All)
            .RuleFor(x => x.ExpiresAt, now.Plus(Duration.FromHours(1)))
            .RuleFor(x => x.ExpirySource, "time")
            .RuleFor(x => x.QueryCount, 0)
            .RuleFor(x => x.CreatedAt, now)
            .RuleFor(x => x.CreatedBy, createdBy ?? Guid.NewGuid())
            .RuleFor(x => x.UpdatedAt, now)
            .RuleFor(x => x.AgentWrappedVaultKey, GrantEnvelopeTestData.AgentVaultKey(
                resolvedOrganizationId,
                resolvedVaultId,
                resolvedId,
                resolvedAgentId,
                agentAccessEpoch));
    }
}
