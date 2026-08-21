using JetBrains.Annotations;
using Microsoft.AspNetCore.SignalR;
using Palladin.Module.Notification.Shared;

namespace Palladin.Module.Notification.Infrastructure.SignalR;

[UsedImplicitly]
internal sealed class RealtimeEventNotifier(IHubContext<NotificationHub> hubContext) : IRealtimeEventNotifier
{
    internal const string ReceiveVaultSyncInvalidation = "ReceiveVaultSyncInvalidation";

    public Task NotifyVaultSyncInvalidationAsync(
        IReadOnlyCollection<Guid> userIds,
        VaultSyncInvalidationPayload payload,
        CancellationToken ct)
    {
        if (userIds.Count == 0)
        {
            return Task.CompletedTask;
        }

        return hubContext.Clients
            .Groups(userIds.Distinct().Select(NotificationGroups.User).ToList())
            .SendAsync(ReceiveVaultSyncInvalidation, payload, ct);
    }
}
