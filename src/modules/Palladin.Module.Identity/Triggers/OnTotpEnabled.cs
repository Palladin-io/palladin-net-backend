using Palladin.Core.Analytics;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Identity.Triggers;

[UsedImplicitly]
internal sealed class OnTotpEnabledDefinition : ConsumerDefinition<OnTotpEnabled>
{
    public OnTotpEnabledDefinition() => EndpointName = IdentityEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnTotpEnabled(IAnalyticsService analyticsService) : IConsumer<TotpEnabledEvent>
{
    public Task Consume(ConsumeContext<TotpEnabledEvent> context)
    {
        analyticsService.CaptureEvent(context.Message.UserId.ToString(), "identity", "totp-enabled");
        return Task.CompletedTask;
    }
}
