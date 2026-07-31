using System.Security.Cryptography;
using Palladin.Core.Types;
using Palladin.Module.Agents.Domain;
using NodaTime;

namespace Palladin.Tests.Integrations.Shared.Fakers;

internal static class AgentFaker
{
    public static string GeneratePublicKey() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public static PrivateCtorFaker<Agent> Create(
        Guid? id = null,
        Guid? organizationId = null,
        string? publicKey = null,
        string? signingPublicKey = null,
        string? type = null,
        Guid? lastUsedApiKeyId = null) =>
        (PrivateCtorFaker<Agent>)new PrivateCtorFaker<Agent>()
            .RuleFor(x => x.Id, id ?? Guid.NewGuid())
            .RuleFor(x => x.OrganizationId, organizationId ?? Guid.NewGuid())
            .RuleFor(x => x.PublicKey, publicKey ?? GeneratePublicKey())
            .RuleFor(x => x.SigningPublicKey, signingPublicKey ?? GeneratePublicKey())
            .RuleFor(x => x.RecipientKeyVersion, 1u)
            .RuleFor(x => x.AccessEpoch, 1u)
            .RuleFor(x => x.Name, f => f.Internet.UserName())
            .RuleFor(x => x.Type, type ?? "assistant")
            .RuleFor(x => x.Status, AgentStatus.Active)
            .RuleFor(x => x.LastUsedApiKeyId, lastUsedApiKeyId)
            .RuleFor(x => x.CreatedAt, SystemClock.Instance.GetCurrentInstant())
            .RuleFor(x => x.EnrolledAt, SystemClock.Instance.GetCurrentInstant());
}
