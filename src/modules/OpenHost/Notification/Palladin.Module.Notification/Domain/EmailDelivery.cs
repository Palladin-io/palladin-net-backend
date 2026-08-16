using NodaTime;

namespace Palladin.Module.Notification.Domain;

internal enum EmailDeliveryStatus : short
{
    // Zero intentionally preserves the meaning of rows created before the state machine existed.
    Sent = 0,
    Dispatching = 1,
}

internal sealed class EmailDelivery
{
    public string IdempotencyKey { get; private set; } = string.Empty;
    public EmailDeliveryStatus Status { get; private set; }
    public Guid? DispatchToken { get; private set; }
    public Instant? DispatchLeaseExpiresAt { get; private set; }
    public Instant? SentAt { get; private set; }
    public Instant UpdatedAt { get; private set; }

    private EmailDelivery() { }

    internal static EmailDelivery Claim(
        string idempotencyKey,
        Guid dispatchToken,
        Instant now,
        Duration lease) =>
        new()
        {
            IdempotencyKey = idempotencyKey,
            Status = EmailDeliveryStatus.Dispatching,
            DispatchToken = dispatchToken,
            DispatchLeaseExpiresAt = now + lease,
            UpdatedAt = now,
        };

    internal bool TryReclaim(Guid dispatchToken, Instant now, Duration lease)
    {
        if (Status == EmailDeliveryStatus.Sent
            || DispatchLeaseExpiresAt is { } leaseExpiresAt && leaseExpiresAt > now)
        {
            return false;
        }

        Status = EmailDeliveryStatus.Dispatching;
        DispatchToken = dispatchToken;
        DispatchLeaseExpiresAt = now + lease;
        UpdatedAt = now;
        return true;
    }

    internal void MarkSent(Guid dispatchToken, Instant sentAt)
    {
        if (Status != EmailDeliveryStatus.Dispatching || DispatchToken != dispatchToken)
        {
            throw new InvalidOperationException("Only the current email dispatch owner can mark delivery as sent.");
        }

        Status = EmailDeliveryStatus.Sent;
        DispatchToken = null;
        DispatchLeaseExpiresAt = null;
        SentAt = sentAt;
        UpdatedAt = sentAt;
    }
}
