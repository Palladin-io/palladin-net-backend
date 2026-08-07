using Palladin.Core.Analytics;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Vault.Triggers;

[UsedImplicitly]
internal sealed class OnGrantRevokedDefinition : ConsumerDefinition<OnGrantRevoked>
{
    public OnGrantRevokedDefinition() => EndpointName = VaultEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnGrantRevoked(IAnalyticsService analyticsService) : IConsumer<GrantRevokedEvent>
{
    public Task Consume(ConsumeContext<GrantRevokedEvent> context)
    {
        var msg = context.Message;
        var distinctId = msg.RevokedBy?.ToString() ?? "system";
        analyticsService.CaptureEvent(distinctId, "vault", "grant-revoked", new Dictionary<string, object>
        {
            ["vault_id"] = msg.VaultId,
            ["grant_type"] = msg.Type.ToString(),
            ["duration_active_seconds"] = msg.DurationActiveSeconds,
            ["revoked_by_system"] = msg.RevokedBySystem,
        });
        return Task.CompletedTask;
    }
}
