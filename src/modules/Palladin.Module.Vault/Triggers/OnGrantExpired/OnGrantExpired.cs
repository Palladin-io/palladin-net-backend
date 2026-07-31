using Palladin.Core.Analytics;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Vault.Triggers;

[UsedImplicitly]
internal sealed class OnGrantExpiredDefinition : ConsumerDefinition<OnGrantExpired>
{
    public OnGrantExpiredDefinition() => EndpointName = VaultEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnGrantExpired(IAnalyticsService analyticsService) : IConsumer<GrantExpiredEvent>
{
    // System-initiated expiry — no user actor (consistent with system cascade-revoke). distinctId = "system".
    private const string SystemActor = "system";

    public Task Consume(ConsumeContext<GrantExpiredEvent> context)
    {
        var msg = context.Message;
        var properties = new Dictionary<string, object>
        {
            ["vault_id"] = msg.VaultId,
            ["grant_type"] = msg.Type.ToString(),
        };

        if (msg.TtlSeconds is not null)
        {
            properties["ttl_seconds"] = msg.TtlSeconds.Value;
        }

        analyticsService.CaptureEvent(SystemActor, "vault", "grant-expired", properties);
        return Task.CompletedTask;
    }
}
