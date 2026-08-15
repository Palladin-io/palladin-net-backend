using NodaTime;
using Palladin.Core.Types;
using Palladin.Core.Types.Exceptions;

namespace Palladin.Module.Vault.Domain;

internal sealed class FullGrantPreparation
{
    internal Guid OrganizationId { get; private set; }
    internal Guid VaultId { get; private set; }
    internal Guid Id { get; private set; }
    internal Guid AgentId { get; private set; }
    internal uint AgentAccessEpoch { get; private set; }
    internal string AgentPublicKey { get; private set; } = string.Empty;
    internal uint RecipientAgentKeyVersion { get; private set; }
    internal byte[] AgentKeyFingerprint { get; private set; } = [];
    internal uint MemberKeyGeneration { get; private set; }
    internal GrantMethods Methods { get; private set; }
    internal Instant? GrantExpiresAt { get; private set; }
    internal int? QueryLimit { get; private set; }
    internal string ExpirySource { get; private set; } = string.Empty;
    internal Guid CreatedBy { get; private set; }
    internal Instant CreatedAt { get; private set; }
    internal Instant PreparationExpiresAt { get; private set; }
    internal ICollection<FullGrantPreparationEntry> Entries { get; private set; } = [];

    private FullGrantPreparation() { }

    internal static FullGrantPreparation Create(
        Guid organizationId,
        Guid vaultId,
        Guid grantId,
        Guid agentId,
        uint agentAccessEpoch,
        string agentPublicKey,
        uint recipientAgentKeyVersion,
        byte[] agentKeyFingerprint,
        uint memberKeyGeneration,
        GrantMethods methods,
        Instant? grantExpiresAt,
        int? queryLimit,
        string expirySource,
        Guid createdBy,
        Instant createdAt,
        Instant preparationExpiresAt)
    {
        if (organizationId == Guid.Empty || vaultId == Guid.Empty || grantId == Guid.Empty
            || agentId == Guid.Empty || agentAccessEpoch == 0 || string.IsNullOrWhiteSpace(agentPublicKey)
            || recipientAgentKeyVersion == 0 || agentKeyFingerprint.Length != VaultProtocol.FingerprintBytes
            || memberKeyGeneration == 0 || !methods.IsValidSet() || createdBy == Guid.Empty
            || preparationExpiresAt <= createdAt || queryLimit is <= 0
            || (grantExpiresAt.HasValue && queryLimit.HasValue))
        {
            throw new DomainException("Full grant preparation is invalid.");
        }

        return new FullGrantPreparation
        {
            OrganizationId = organizationId,
            VaultId = vaultId,
            Id = grantId,
            AgentId = agentId,
            AgentAccessEpoch = agentAccessEpoch,
            AgentPublicKey = agentPublicKey,
            RecipientAgentKeyVersion = recipientAgentKeyVersion,
            AgentKeyFingerprint = agentKeyFingerprint.ToArray(),
            MemberKeyGeneration = memberKeyGeneration,
            Methods = methods,
            GrantExpiresAt = grantExpiresAt,
            QueryLimit = queryLimit,
            ExpirySource = expirySource,
            CreatedBy = createdBy,
            CreatedAt = createdAt,
            PreparationExpiresAt = preparationExpiresAt,
        };
    }

    internal bool IsExactRetry(
        Guid agentId,
        GrantMethods methods,
        Instant? grantExpiresAt,
        int? queryLimit,
        Guid createdBy) =>
        AgentId == agentId
        && Methods == methods
        && GrantExpiresAt == grantExpiresAt
        && QueryLimit == queryLimit
        && CreatedBy == createdBy;

}
