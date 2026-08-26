using JetBrains.Annotations;
using MassTransit;
using Palladin.Core.Analytics;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Infrastructure.MassTransit;

namespace Palladin.Module.Identity.Triggers;

[UsedImplicitly]
internal sealed class OnWaitlistDeveloperBenefitActivatedAnalyticsDefinition
    : ConsumerDefinition<OnWaitlistDeveloperBenefitActivatedAnalytics>
{
    public OnWaitlistDeveloperBenefitActivatedAnalyticsDefinition() =>
        EndpointName = IdentityEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnWaitlistDeveloperBenefitActivatedAnalytics(
    IAnalyticsService analyticsService)
    : IConsumer<WaitlistDeveloperBenefitActivatedEvent>
{
    public Task Consume(ConsumeContext<WaitlistDeveloperBenefitActivatedEvent> context)
    {
        analyticsService.CaptureEvent(
            context.Message.UserId.ToString(),
            "identity",
            "waitlist-benefit-activated",
            new Dictionary<string, object> { ["plan"] = "Developer" });
        return Task.CompletedTask;
    }
}
