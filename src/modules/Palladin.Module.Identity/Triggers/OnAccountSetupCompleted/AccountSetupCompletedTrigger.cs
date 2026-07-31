using Palladin.Core.Analytics;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Identity.Triggers;

[UsedImplicitly]
internal sealed class AccountSetupCompletedTriggerDefinition : ConsumerDefinition<AccountSetupCompletedTrigger>
{
    public AccountSetupCompletedTriggerDefinition() => EndpointName = IdentityEndpoints.Self;
}

[UsedImplicitly]
internal sealed class AccountSetupCompletedTrigger(IAnalyticsService analyticsService) : IConsumer<AccountSetupCompletedEvent>
{
    public Task Consume(ConsumeContext<AccountSetupCompletedEvent> context)
    {
        var msg = context.Message;
        analyticsService.CaptureEvent(msg.UserId.ToString(), "identity", "account-setup-completed");
        return Task.CompletedTask;
    }
}
