using Palladin.Core.Analytics;
using Palladin.Module.Notification.Contracts.Events;
using Palladin.Module.Notification.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Notification.Triggers;

[UsedImplicitly]
internal sealed class OnPushNotificationSentDefinition : ConsumerDefinition<OnPushNotificationSent>
{
    public OnPushNotificationSentDefinition() => EndpointName = NotificationEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnPushNotificationSent(IAnalyticsService analyticsService) : IConsumer<PushNotificationSentEvent>
{
    public Task Consume(ConsumeContext<PushNotificationSentEvent> context)
    {
        var msg = context.Message;
        analyticsService.CaptureEvent(msg.OrganizationId.ToString(), "notification", "push-sent", new Dictionary<string, object>
        {
            ["event_type"] = msg.Type,
            ["success_count"] = msg.SuccessCount,
            ["failure_count"] = msg.FailureCount,
        });
        return Task.CompletedTask;
    }
}
