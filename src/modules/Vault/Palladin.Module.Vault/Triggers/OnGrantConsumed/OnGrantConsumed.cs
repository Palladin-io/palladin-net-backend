using Palladin.Core.Analytics;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Vault.Triggers;

[UsedImplicitly]
internal sealed class OnGrantConsumedDefinition : ConsumerDefinition<OnGrantConsumed>
{
    public OnGrantConsumedDefinition() => EndpointName = VaultEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnGrantConsumed(IAnalyticsService analyticsService) : IConsumer<GrantConsumedEvent>
{
    public Task Consume(ConsumeContext<GrantConsumedEvent> context)
    {
        var msg = context.Message;
        analyticsService.CaptureEvent(msg.AgentId.ToString(), "vault", "grant-consumed", new Dictionary<string, object>
        {
            ["vault_id"] = msg.VaultId,
        });
        return Task.CompletedTask;
    }
}
