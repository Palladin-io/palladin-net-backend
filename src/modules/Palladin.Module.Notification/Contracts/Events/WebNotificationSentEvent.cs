using Palladin.Core.Events;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Notification.Contracts.Events;

[PublicAPI]
public sealed record WebNotificationSentEvent(
    Guid OrganizationId,
    string Type,
    Instant UpdatedAt) : IIntegrationEvent;
