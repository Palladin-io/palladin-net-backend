using Palladin.Core.Analytics;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Identity.Triggers;

[UsedImplicitly]
internal sealed class OnLoginLockedOutDefinition : ConsumerDefinition<OnLoginLockedOut>
{
    public OnLoginLockedOutDefinition() => EndpointName = IdentityEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnLoginLockedOut(IAnalyticsService analyticsService) : IConsumer<LoginLockedOutEvent>
{
    public Task Consume(ConsumeContext<LoginLockedOutEvent> context)
    {
        // EmailHash is the distinct id — a lockout may target an unknown email and the address itself
        // stays out of analytics.
        analyticsService.CaptureEvent(context.Message.EmailHash, "identity", "login-locked-out");
        return Task.CompletedTask;
    }
}
