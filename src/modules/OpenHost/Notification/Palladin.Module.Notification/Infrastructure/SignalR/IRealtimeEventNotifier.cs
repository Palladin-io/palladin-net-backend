using Palladin.Module.Notification.Shared;

namespace Palladin.Module.Notification.Infrastructure.SignalR;

internal interface IRealtimeEventNotifier
{
    Task NotifyVaultSyncInvalidationAsync(
        IReadOnlyCollection<Guid> userIds,
        VaultSyncInvalidationPayload payload,
        CancellationToken ct);
}
