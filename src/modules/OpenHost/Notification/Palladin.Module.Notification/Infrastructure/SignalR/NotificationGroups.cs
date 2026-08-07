namespace Palladin.Module.Notification.Infrastructure.SignalR;

internal static class NotificationGroups
{
    internal static string Organization(Guid organizationId) => $"org:{organizationId}";

    internal static string User(Guid userId) => $"user:{userId}";
}
