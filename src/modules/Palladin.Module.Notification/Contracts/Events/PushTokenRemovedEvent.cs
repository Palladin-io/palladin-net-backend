using Palladin.Core.Events;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Notification.Contracts.Events;

// A user removed (signed out / un-registered) one of their device push tokens. Consumed by Analytics.
[PublicAPI]
public sealed record PushTokenRemovedEvent(
    Guid TokenId,
    Guid UserId,
    Instant UpdatedAt) : IIntegrationEvent;
