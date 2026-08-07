using Palladin.Core.Analytics;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Vault.Triggers;

[UsedImplicitly]
internal sealed class OnGrantDeniedDefinition : ConsumerDefinition<OnGrantDenied>
{
    public OnGrantDeniedDefinition() => EndpointName = VaultEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnGrantDenied(IAnalyticsService analyticsService) : IConsumer<GrantDeniedEvent>
{
    public Task Consume(ConsumeContext<GrantDeniedEvent> context)
    {
        var msg = context.Message;
        analyticsService.CaptureEvent(msg.DeniedBy.ToString(), "vault", "grant-denied", new Dictionary<string, object>
        {
            ["vault_id"] = msg.VaultId,
        });
        return Task.CompletedTask;
    }
}
