using System.Security.Cryptography;
using Palladin.Core.Types;
using NodaTime;
using VaultAgent = Palladin.Module.Vault.Domain.Agent;

namespace Palladin.Tests.Integrations.Shared.Fakers;

internal static class VaultAgentFaker
{
    public static PrivateCtorFaker<VaultAgent> Create(
        Guid? id = null,
        Guid? organizationId = null,
        AgentStatus status = AgentStatus.Active,
        string? publicKey = null,
        string? signingPublicKey = null,
        uint recipientKeyVersion = 1,
        uint? accessEpoch = null,
        string? iconKey = null,
        string? iconColor = null,
        Instant? updatedAt = null) =>
        (PrivateCtorFaker<VaultAgent>)new PrivateCtorFaker<VaultAgent>()
            .RuleFor(x => x.Id, id ?? Guid.NewGuid())
            .RuleFor(x => x.OrganizationId, organizationId ?? Guid.NewGuid())
            .RuleFor(x => x.Status, status)
            .RuleFor(x => x.PublicKey, publicKey ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))
            .RuleFor(x => x.SigningPublicKey, signingPublicKey ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))
            .RuleFor(x => x.RecipientKeyVersion, recipientKeyVersion)
            .RuleFor(x => x.Name, f => f.Internet.UserName())
            .RuleFor(x => x.IconKey, iconKey)
            .RuleFor(x => x.IconColor, iconColor)
            .RuleFor(x => x.UpdatedAt, updatedAt ?? SystemClock.Instance.GetCurrentInstant())
            .RuleFor(x => x.AccessEpoch, (_, agent) =>
                accessEpoch ?? (agent.Status == AgentStatus.Pending ? 0u : 1u))
            .RuleFor(x => x.AccessEpochStartedAt, (_, agent) =>
                agent.Status == AgentStatus.Active ? agent.UpdatedAt : null);
}
