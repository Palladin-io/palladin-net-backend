using JetBrains.Annotations;
using NodaTime;
using Palladin.Core.Events;

namespace Palladin.Module.Agents.Contracts.Events;

[PublicAPI]
public sealed record AgentDeactivationRequestedEvent(
    Guid RequestId,
    Guid AgentId,
    Guid OrganizationId,
    Guid RequestedBy,
    Instant RequestedAt) : IIntegrationEvent;
