using Palladin.Core.Events;
using Palladin.Core.Security;
using Palladin.Core.Types;
using JetBrains.Annotations;
using NodaTime;
using Palladin.Module.Notification.Contracts.ValueObjects;

namespace Palladin.Module.Notification.Contracts.Commands;

[PublicAPI]
public sealed record BroadcastNotificationCommand(
    Guid OrganizationId,
    NotificationType Type,
    NotificationCategory Category,
    string TitleKey,
    IReadOnlyDictionary<string, string> Metadata,
    IReadOnlyList<NotificationScope> Scopes,
    Permission? RequiredPermission,
    Guid SubjectId,
    Instant OccurredAt,
    bool Collapsible = false,
    bool CollapsesPending = false) : IIntegrationCommand;
