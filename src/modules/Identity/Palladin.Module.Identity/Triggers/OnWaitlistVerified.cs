using Palladin.Core.Analytics;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Identity.Triggers;

[UsedImplicitly]
internal sealed class OnWaitlistVerifiedDefinition : ConsumerDefinition<OnWaitlistVerified>
{
    public OnWaitlistVerifiedDefinition() => EndpointName = IdentityEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnWaitlistVerified(IAnalyticsService analyticsService) : IConsumer<WaitlistVerifiedEvent>
{
    public Task Consume(ConsumeContext<WaitlistVerifiedEvent> context)
    {
        analyticsService.CaptureEvent(context.Message.EntryId.ToString(), "identity", "waitlist-verified", new Dictionary<string, object>
        {
            ["occurred_at"] = context.Message.OccurredAt.ToString(),
        });
        return Task.CompletedTask;
    }
}
