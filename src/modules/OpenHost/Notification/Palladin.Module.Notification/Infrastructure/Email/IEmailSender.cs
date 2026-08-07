namespace Palladin.Module.Notification.Infrastructure.Email;

// The email channel of the Notification module (alongside SignalR and FCM). The concrete provider
// (SES today) is swappable via DI without touching callers.
internal interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct);
}
