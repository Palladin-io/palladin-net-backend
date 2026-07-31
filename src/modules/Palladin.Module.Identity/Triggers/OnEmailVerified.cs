using Palladin.Core.Analytics;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Identity.Triggers;

[UsedImplicitly]
internal sealed class OnEmailVerifiedDefinition : ConsumerDefinition<OnEmailVerified>
{
    public OnEmailVerifiedDefinition() => EndpointName = IdentityEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnEmailVerified(IAnalyticsService analyticsService) : IConsumer<EmailVerifiedEvent>
{
    public Task Consume(ConsumeContext<EmailVerifiedEvent> context)
    {
        analyticsService.CaptureEvent(context.Message.UserId.ToString(), "identity", "email-verified");
        return Task.CompletedTask;
    }
}
