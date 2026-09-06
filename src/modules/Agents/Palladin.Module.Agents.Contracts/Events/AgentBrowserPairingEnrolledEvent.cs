using JetBrains.Annotations;
using NodaTime;
using Palladin.Core.Events;

namespace Palladin.Module.Agents.Contracts.Events;

[PublicAPI]
public sealed record AgentBrowserPairingEnrolledEvent(
    Guid AgentId,
    Guid OrganizationId,
    string AgentName,
    Instant OccurredAt) : IIntegrationEvent;
