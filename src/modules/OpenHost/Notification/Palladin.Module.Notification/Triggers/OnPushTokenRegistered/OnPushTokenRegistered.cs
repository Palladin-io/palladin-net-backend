using Palladin.Core.Analytics;
using Palladin.Module.Notification.Contracts.Events;
using Palladin.Module.Notification.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Notification.Triggers;

[UsedImplicitly]
internal sealed class OnPushTokenRegisteredDefinition : ConsumerDefinition<OnPushTokenRegistered>
{
    public OnPushTokenRegisteredDefinition() => EndpointName = NotificationEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnPushTokenRegistered(IAnalyticsService analyticsService) : IConsumer<PushTokenRegisteredEvent>
{
    public Task Consume(ConsumeContext<PushTokenRegisteredEvent> context)
    {
        var msg = context.Message;
        analyticsService.CaptureEvent(msg.UserId.ToString(), "notification", "push-token-registered", new Dictionary<string, object>
        {
            ["platform"] = msg.Platform.ToString(),
            ["organization_id"] = msg.OrganizationId,
        });
        return Task.CompletedTask;
    }
}
