using Palladin.Core.Analytics;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Identity.Triggers;

[UsedImplicitly]
internal sealed class UserSignedUpTriggerDefinition : ConsumerDefinition<UserSignedUpTrigger>
{
    public UserSignedUpTriggerDefinition() => EndpointName = IdentityEndpoints.Self;
}

[UsedImplicitly]
internal sealed class UserSignedUpTrigger(IAnalyticsService analyticsService) : IConsumer<UserSignedUpEvent>
{
    public Task Consume(ConsumeContext<UserSignedUpEvent> context)
    {
        var msg = context.Message;
        analyticsService.CaptureEvent(msg.UserId.ToString(), "identity", "user-signed-up", new Dictionary<string, object>
        {
            ["provider"] = msg.Provider,
        });
        return Task.CompletedTask;
    }
}
