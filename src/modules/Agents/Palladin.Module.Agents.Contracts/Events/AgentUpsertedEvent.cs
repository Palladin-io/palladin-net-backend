using Palladin.Core.Events;
using Palladin.Core.Types;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Agents.Contracts.Events;

[PublicAPI]
public sealed record AgentUpsertedEvent(
    Guid AgentId,
    Guid OrganizationId,
    AgentStatus Status,
    string PublicKey,
    uint RecipientKeyVersion,
    string SigningPublicKey,
    string? Name,
    string? Type,
    string? IconKey,
    string? IconColor,
    uint AccessEpoch,
    Instant? AccessEpochStartedAt,
    Instant UpdatedAt) : IIntegrationEvent;
