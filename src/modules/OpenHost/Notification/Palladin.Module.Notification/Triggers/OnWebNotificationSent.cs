using Palladin.Core.Analytics;
using Palladin.Module.Notification.Contracts.Events;
using Palladin.Module.Notification.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Notification.Triggers;

[UsedImplicitly]
internal sealed class OnWebNotificationSentDefinition : ConsumerDefinition<OnWebNotificationSent>
{
    public OnWebNotificationSentDefinition() => EndpointName = NotificationEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnWebNotificationSent(IAnalyticsService analyticsService) : IConsumer<WebNotificationSentEvent>
{
    public Task Consume(ConsumeContext<WebNotificationSentEvent> context)
    {
        var msg = context.Message;
        analyticsService.CaptureEvent(msg.OrganizationId.ToString(), "notification", "signalr-sent", new Dictionary<string, object>
        {
            ["event_type"] = msg.Type,
        });
        return Task.CompletedTask;
    }
}
