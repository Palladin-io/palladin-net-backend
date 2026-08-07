using Palladin.Module.Notification.Domain;

namespace Palladin.Module.Notification.Infrastructure.Email.Events;

// Kind is a coarse label for logging/metrics only (e.g. "Bounce/Permanent", "Complaint", "Delivery").
// Enveloped=true means the payload arrived wrapped in an SNS envelope (RawMessageDelivery disabled
// on the subscription) and had to be unwrapped — the subscription should be fixed.
internal sealed record SesEventParseResult(
    string Kind,
    IReadOnlyList<SesSuppression> Suppressions,
    string? MessageId,
    bool Enveloped = false);

internal sealed record SesSuppression(string Address, SuppressionReason Reason);
