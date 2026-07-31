using JetBrains.Annotations;
using NodaTime;
using Palladin.Core.Events;

namespace Palladin.Module.Identity.Contracts.Events;

[PublicAPI]
public sealed record OrganizationMemberRemovalRequestedEvent(
    Guid RequestId,
    Guid OrganizationId,
    Guid UserId,
    Guid RequestedBy,
    Instant RequestedAt) : IIntegrationEvent;
