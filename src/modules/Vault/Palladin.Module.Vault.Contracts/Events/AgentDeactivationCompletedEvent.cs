using JetBrains.Annotations;
using NodaTime;
using Palladin.Core.Events;

namespace Palladin.Module.Vault.Contracts.Events;

[PublicAPI]
public sealed record AgentDeactivationCompletedEvent(
    Guid RequestId,
    Guid OrganizationId,
    Guid AgentId,
    Instant CompletedAt,
    Instant UpdatedAt) : IIntegrationEvent;
