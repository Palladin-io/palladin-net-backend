using Palladin.Core.Analytics;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Vault.Triggers;

[UsedImplicitly]
internal sealed class OnGrantSupersededDefinition : ConsumerDefinition<OnGrantSuperseded>
{
    public OnGrantSupersededDefinition() => EndpointName = VaultEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnGrantSuperseded(IAnalyticsService analyticsService) : IConsumer<GrantSupersededEvent>
{
    public Task Consume(ConsumeContext<GrantSupersededEvent> context)
    {
        var msg = context.Message;
        analyticsService.CaptureEvent("system", "vault", "grant-superseded", new Dictionary<string, object>
        {
            ["vault_id"] = msg.VaultId,
            ["grant_type"] = msg.Type.ToString(),
            ["superseded_by_grant_id"] = msg.SupersededByGrantId,
            ["duration_active_seconds"] = msg.DurationActiveSeconds,
        });
        return Task.CompletedTask;
    }
}
