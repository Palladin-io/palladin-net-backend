using Palladin.Core.Analytics;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Identity.Triggers;

[UsedImplicitly]
internal sealed class OnWaitlistJoinedAnalyticsDefinition : ConsumerDefinition<OnWaitlistJoinedAnalytics>
{
    public OnWaitlistJoinedAnalyticsDefinition() => EndpointName = IdentityEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnWaitlistJoinedAnalytics(IAnalyticsService analyticsService) : IConsumer<WaitlistJoinedEvent>
{
    public Task Consume(ConsumeContext<WaitlistJoinedEvent> context)
    {
        var msg = context.Message;
        // Entry id as distinct id — there is no account yet and the email itself stays out of analytics.
        analyticsService.CaptureEvent(msg.EntryId.ToString(), "identity", "waitlist-joined", new Dictionary<string, object>
        {
            ["language"] = msg.Language,
        });
        return Task.CompletedTask;
    }
}
