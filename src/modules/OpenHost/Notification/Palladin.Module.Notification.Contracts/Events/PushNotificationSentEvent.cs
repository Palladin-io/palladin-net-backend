using Palladin.Core.Events;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Notification.Contracts.Events;

// A push notification was dispatched to the FCM provider for an organization. Consumed by Analytics.
// SuccessCount/FailureCount reflect the provider response, not delivery to the device.
[PublicAPI]
public sealed record PushNotificationSentEvent(
    Guid OrganizationId,
    string Type,
    int SuccessCount,
    int FailureCount,
    Instant UpdatedAt) : IIntegrationEvent;
