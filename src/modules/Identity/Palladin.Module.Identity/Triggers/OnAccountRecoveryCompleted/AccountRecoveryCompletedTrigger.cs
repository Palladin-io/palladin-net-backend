using Palladin.Core.Analytics;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Identity.Triggers;

[UsedImplicitly]
internal sealed class AccountRecoveryCompletedTriggerDefinition : ConsumerDefinition<AccountRecoveryCompletedTrigger>
{
    public AccountRecoveryCompletedTriggerDefinition() => EndpointName = IdentityEndpoints.Self;
}

[UsedImplicitly]
internal sealed class AccountRecoveryCompletedTrigger(IAnalyticsService analyticsService) : IConsumer<AccountRecoveryCompletedEvent>
{
    public Task Consume(ConsumeContext<AccountRecoveryCompletedEvent> context)
    {
        var msg = context.Message;
        analyticsService.CaptureEvent(msg.UserId.ToString(), "identity", "account-recovery-completed");
        return Task.CompletedTask;
    }
}
