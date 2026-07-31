using Palladin.Core.Events;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Agents.Contracts.Events;

// Dedicated deletion event (separate from AgentDeactivatedEvent) because it triggers cascade removal
// of the agent's grants and read-model replica in the Vault module, plus an audit trail entry.
// DeletedBy carries the operator id so audit can attribute this destructive admin op.
[PublicAPI]
public sealed record AgentDeletedEvent(
    Guid AgentId,
    Guid OrganizationId,
    Guid DeletedBy,
    string DeletedByName,
    string AgentName,
    Instant DeletedAt) : IIntegrationEvent;
