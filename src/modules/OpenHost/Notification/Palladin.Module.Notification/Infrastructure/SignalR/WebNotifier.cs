using Palladin.Core.Events;
using Palladin.Module.Notification.Contracts.Events;
using Palladin.Module.Notification.Shared;
using JetBrains.Annotations;
using Microsoft.AspNetCore.SignalR;
using NodaTime;

namespace Palladin.Module.Notification.Infrastructure.SignalR;

[UsedImplicitly]
internal sealed class WebNotifier(
    IHubContext<NotificationHub> hubContext,
    IEnumerable<IEventPublisher> eventPublishers,
    IClock clock) : IWebNotifier
{
    private const string ReceiveNotification = "ReceiveNotification";

    public async Task NotifyUsersAsync(
        Guid organizationId,
        IReadOnlyCollection<Guid> userIds,
        NotificationPayload payload,
        CancellationToken ct)
    {
        if (userIds.Count == 0)
        {
            return;
        }

        var groups = userIds.Select(NotificationGroups.User).ToList();
        await hubContext.Clients
            .Groups(groups)
            .SendAsync(ReceiveNotification, payload.Type.ToWire(), payload, ct);

        foreach (var publisher in eventPublishers)
        {
            await publisher.PublishAsync(
                new WebNotificationSentEvent(organizationId, payload.Type.ToWire(), clock.GetCurrentInstant()),
                ct);
        }
    }
}
