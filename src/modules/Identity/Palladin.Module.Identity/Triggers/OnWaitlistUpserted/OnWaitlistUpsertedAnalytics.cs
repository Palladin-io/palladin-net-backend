using Palladin.Core.Analytics;
using Palladin.Core.Types;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Identity.Triggers;

[UsedImplicitly]
internal sealed class OnWaitlistUpsertedAnalyticsDefinition : ConsumerDefinition<OnWaitlistUpsertedAnalytics>
{
    public OnWaitlistUpsertedAnalyticsDefinition() => EndpointName = IdentityEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnWaitlistUpsertedAnalytics(IAnalyticsService analyticsService) : IConsumer<WaitlistUpsertedEvent>
{
    public Task Consume(ConsumeContext<WaitlistUpsertedEvent> context)
    {
        var msg = context.Message;
        if (msg.Change != EntityChange.Created)
        {
            return Task.CompletedTask;
        }
        analyticsService.CaptureEvent(msg.EntryId.ToString(), "identity", "waitlist-joined", new Dictionary<string, object>
        {
            ["language"] = msg.Language,
            ["occurred_at"] = msg.OccurredAt.ToString(),
        });
        return Task.CompletedTask;
    }
}
