using NodaTime;

namespace Palladin.Module.Notification.Domain;

// A recipient that must never be emailed again (hard bounce or complaint). The normalized address is
// the natural key, which makes suppression inserts idempotent. No lifecycle events — it is a flat
// materialized fact fed by the SES event pipeline.
internal sealed class SuppressedEmail
{
    public string Address { get; private set; } = string.Empty;
    public SuppressionReason Reason { get; private set; }
    public Instant CreatedAt { get; private set; }

    private SuppressedEmail() { }

    internal static SuppressedEmail Create(string normalizedAddress, SuppressionReason reason, Instant now) =>
        new()
        {
            Address = normalizedAddress,
            Reason = reason,
            CreatedAt = now,
        };
}
