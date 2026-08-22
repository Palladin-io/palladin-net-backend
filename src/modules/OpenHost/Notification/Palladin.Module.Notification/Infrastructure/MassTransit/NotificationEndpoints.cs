namespace Palladin.Module.Notification.Infrastructure.MassTransit;

internal static class NotificationEndpoints
{
    internal const string Self = "notification.events.self";
    internal const string Inbox = "notification.commands.inbox";
    internal const string Realtime = "notification.commands.realtime";
    internal const string Scope = "notification.commands.scope";
    internal const string FromIdentity = "notification.events.identity";
    internal const string Email = "notification.commands.email";
}
