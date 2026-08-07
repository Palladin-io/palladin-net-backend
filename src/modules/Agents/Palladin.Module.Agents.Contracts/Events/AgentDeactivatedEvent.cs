using Palladin.Core.Events;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Agents.Contracts.Events;

// Dedicated deactivation event (separate from AgentUpsertedEvent) because it triggers cascade
// revoke of the agent's active grants in the Vault module.
// DeactivatedBy carries the operator id so audit can attribute this sensitive admin op.
[PublicAPI]
public sealed record AgentDeactivatedEvent(
    Guid AgentId,
    Guid OrganizationId,
    Guid DeactivatedBy,
    string DeactivatedByName,
    string AgentName,
    uint AccessEpoch,
    Instant UpdatedAt) : IIntegrationEvent;
