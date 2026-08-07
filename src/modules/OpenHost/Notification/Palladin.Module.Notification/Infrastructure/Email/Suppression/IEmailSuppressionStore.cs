using Palladin.Module.Notification.Domain;

namespace Palladin.Module.Notification.Infrastructure.Email.Suppression;

internal interface IEmailSuppressionStore
{
    Task<bool> IsSuppressedAsync(string address, CancellationToken ct);

    // Idempotent: suppressing an already-suppressed address is a no-op.
    Task SuppressAsync(string address, SuppressionReason reason, CancellationToken ct);
}
