using Palladin.Core.Types;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Notification.Shared;

[PublicAPI]
public sealed record NotificationPayload(
    Guid SubjectId,
    NotificationType Type,
    NotificationCategory Category,
    string TitleKey,
    IReadOnlyDictionary<string, string> Metadata,
    Instant OccurredAt);
