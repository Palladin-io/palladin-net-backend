using Palladin.Core.Types;
using NodaTime;

namespace Palladin.Module.Notification.Infrastructure.Push;

internal sealed record PushDispatch(
    Guid SubjectId,
    NotificationType Type,
    NotificationCategory Category,
    Instant OccurredAt);
