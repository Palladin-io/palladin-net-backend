namespace Palladin.Module.Notification.Infrastructure.Email;

internal interface IEmailDispatchDeduplicator
{
    Task SendOnceAsync(
        string? idempotencyKey,
        EmailMessage message,
        CancellationToken ct = default);
}
