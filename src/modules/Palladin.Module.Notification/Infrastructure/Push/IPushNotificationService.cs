namespace Palladin.Module.Notification.Infrastructure.Push;

internal interface IPushNotificationService
{
    Task SendToUsersAsync(
        Guid organizationId,
        IReadOnlyCollection<Guid> userIds,
        PushDispatch dispatch,
        CancellationToken ct);
}
