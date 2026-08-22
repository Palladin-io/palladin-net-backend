using JetBrains.Annotations;
using MassTransit;
using Palladin.Core.Analytics;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Infrastructure.MassTransit;

namespace Palladin.Module.Identity.Triggers;

[UsedImplicitly]
internal sealed class OnLoginLockedOutAnalyticsDefinition : ConsumerDefinition<OnLoginLockedOutAnalytics>
{
    public OnLoginLockedOutAnalyticsDefinition() => EndpointName = IdentityEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnLoginLockedOutAnalytics(IAnalyticsService analyticsService)
    : IConsumer<LoginLockedOutEvent>
{
    public Task Consume(ConsumeContext<LoginLockedOutEvent> context)
    {
        analyticsService.CaptureEvent(context.Message.EmailHash, "identity", "login-locked-out");
        return Task.CompletedTask;
    }
}
