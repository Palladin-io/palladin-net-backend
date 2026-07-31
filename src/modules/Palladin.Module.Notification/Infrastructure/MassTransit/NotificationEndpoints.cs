namespace Palladin.Module.Notification.Infrastructure.MassTransit;

internal static class NotificationEndpoints
{
    internal const string Self = "notification.events.self";
    internal const string Inbox = "notification.commands.inbox";
    internal const string Scope = "notification.events.scope";
    internal const string FromIdentity = "notification.events.identity";
    internal const string Onboarding = "notification.onboarding";
    internal const string Email = "notification.commands.email";
}
