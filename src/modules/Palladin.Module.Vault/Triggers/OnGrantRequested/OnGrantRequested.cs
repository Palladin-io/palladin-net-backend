using Palladin.Core.Analytics;
using Palladin.Core.Types;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Vault.Triggers;

[UsedImplicitly]
internal sealed class OnGrantRequestedDefinition : ConsumerDefinition<OnGrantRequested>
{
    public OnGrantRequestedDefinition() => EndpointName = VaultEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnGrantRequested(IAnalyticsService analyticsService) : IConsumer<GrantRequestedEvent>
{
    public Task Consume(ConsumeContext<GrantRequestedEvent> context)
    {
        var msg = context.Message;
        analyticsService.CaptureEvent(msg.AgentId.ToString(), "vault", "grant-requested", new Dictionary<string, object>
        {
            ["vault_id"] = msg.VaultId,
            ["grant_type"] = GrantType.Granular.ToString(),
        });
        return Task.CompletedTask;
    }
}
