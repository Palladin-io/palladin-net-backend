using Palladin.Core.Analytics;
using Palladin.Module.Notification.Contracts.Events;
using Palladin.Module.Notification.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Notification.Triggers;

[UsedImplicitly]
internal sealed class OnPushTokenRemovedDefinition : ConsumerDefinition<OnPushTokenRemoved>
{
    public OnPushTokenRemovedDefinition() => EndpointName = NotificationEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnPushTokenRemoved(IAnalyticsService analyticsService) : IConsumer<PushTokenRemovedEvent>
{
    public Task Consume(ConsumeContext<PushTokenRemovedEvent> context)
    {
        var msg = context.Message;
        analyticsService.CaptureEvent(msg.UserId.ToString(), "notification", "push-token-removed");
        return Task.CompletedTask;
    }
}
