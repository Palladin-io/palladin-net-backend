using Palladin.Core.Events;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Agents.Contracts.Events;

// Dedicated reactivation event (separate from AgentUpsertedEvent) — restores access to a previously
// deactivated agent, same blast radius as Deactivate so audit attribution to the operator is required.
// ReactivatedBy carries the operator id so the Audit consumer can attribute the row.
[PublicAPI]
public sealed record AgentReactivatedEvent(
    Guid AgentId,
    Guid OrganizationId,
    Guid ReactivatedBy,
    string ReactivatedByName,
    string AgentName,
    Instant UpdatedAt) : IIntegrationEvent;
