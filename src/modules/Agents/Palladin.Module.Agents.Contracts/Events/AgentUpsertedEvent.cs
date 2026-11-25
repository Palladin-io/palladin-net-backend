using Palladin.Core.Events;
using Palladin.Core.Types;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Agents.Contracts.Events;

// PublicKey is the Agent's stable identifier (X-Agent-Key). EncryptionPublicKey/EncryptionKeySuiteId and
// RecipientKeyVersion are the authenticated recipient key publication for every Agent-addressed wrapper.
[PublicAPI]
public sealed record AgentUpsertedEvent(
    Guid AgentId,
    Guid OrganizationId,
    AgentStatus Status,
    string PublicKey,
    uint RecipientKeyVersion,
    string EncryptionPublicKey,
    string EncryptionKeySuiteId,
    string SigningPublicKey,
    string? Name,
    string? Type,
    string? IconKey,
    string? IconColor,
    uint AccessEpoch,
    Instant? AccessEpochStartedAt,
    Instant UpdatedAt) : IIntegrationEvent;
