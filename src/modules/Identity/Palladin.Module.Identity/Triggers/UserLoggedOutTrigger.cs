using Palladin.Core.Analytics;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Identity.Triggers;

[UsedImplicitly]
internal sealed class UserLoggedOutTriggerDefinition : ConsumerDefinition<UserLoggedOutTrigger>
{
    public UserLoggedOutTriggerDefinition() => EndpointName = IdentityEndpoints.Self;
}

[UsedImplicitly]
internal sealed class UserLoggedOutTrigger(IAnalyticsService analyticsService) : IConsumer<UserLoggedOutEvent>
{
    public Task Consume(ConsumeContext<UserLoggedOutEvent> context)
    {
        var msg = context.Message;
        analyticsService.CaptureEvent(msg.UserId.ToString(), "identity", "user-logged-out");
        return Task.CompletedTask;
    }
}
