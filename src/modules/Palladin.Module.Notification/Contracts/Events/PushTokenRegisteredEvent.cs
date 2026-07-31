using Palladin.Core.Events;
using Palladin.Core.Types;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Notification.Contracts.Events;

// A user registered or refreshed a device push token. Consumed by Analytics.
[PublicAPI]
public sealed record PushTokenRegisteredEvent(
    Guid TokenId,
    Guid UserId,
    Guid OrganizationId,
    PushPlatform Platform,
    Instant UpdatedAt) : IIntegrationEvent;
