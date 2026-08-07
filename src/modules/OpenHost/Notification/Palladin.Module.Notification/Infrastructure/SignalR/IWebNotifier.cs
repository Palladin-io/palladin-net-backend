using Palladin.Module.Notification.Shared;

namespace Palladin.Module.Notification.Infrastructure.SignalR;

internal interface IWebNotifier
{
    Task NotifyUsersAsync(
        Guid organizationId,
        IReadOnlyCollection<Guid> userIds,
        NotificationPayload payload,
        CancellationToken ct);
}
