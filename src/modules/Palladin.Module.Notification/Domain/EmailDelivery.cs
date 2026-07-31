using NodaTime;

namespace Palladin.Module.Notification.Domain;

internal sealed class EmailDelivery
{
    public string IdempotencyKey { get; private set; } = string.Empty;
    public Instant SentAt { get; private set; }

    private EmailDelivery() { }

    internal static EmailDelivery Create(string idempotencyKey, Instant sentAt) =>
        new() { IdempotencyKey = idempotencyKey, SentAt = sentAt };
}
