using Palladin.Core.Analytics;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Identity.Triggers;

[UsedImplicitly]
internal sealed class UserLoggedInTriggerDefinition : ConsumerDefinition<UserLoggedInTrigger>
{
    public UserLoggedInTriggerDefinition() => EndpointName = IdentityEndpoints.Self;
}

[UsedImplicitly]
internal sealed class UserLoggedInTrigger(IAnalyticsService analyticsService) : IConsumer<UserLoggedInEvent>
{
    public Task Consume(ConsumeContext<UserLoggedInEvent> context)
    {
        var msg = context.Message;
        analyticsService.CaptureEvent(msg.UserId.ToString(), "identity", "user-logged-in", new Dictionary<string, object>
        {
            ["provider"] = msg.Provider,
            ["is_new_user"] = msg.IsNewUser,
        });
        return Task.CompletedTask;
    }
}
