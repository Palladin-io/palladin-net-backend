using Palladin.Core.Analytics;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Vault.Triggers;

[UsedImplicitly]
internal sealed class OnGrantCreatedDefinition : ConsumerDefinition<OnGrantCreated>
{
    public OnGrantCreatedDefinition() => EndpointName = VaultEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnGrantCreated(IAnalyticsService analyticsService) : IConsumer<GrantCreatedEvent>
{
    public Task Consume(ConsumeContext<GrantCreatedEvent> context)
    {
        var msg = context.Message;
        var properties = new Dictionary<string, object>
        {
            ["vault_id"] = msg.VaultId,
            ["approved_type"] = msg.Type.ToString(),
            ["expiry_source"] = msg.ExpirySource,
        };

        if (msg.ExpiresAt is not null)
        {
            properties["ttl_seconds"] = (long)(msg.ExpiresAt.Value - msg.UpdatedAt).TotalSeconds;
        }

        if (msg.QueryLimit is not null)
        {
            properties["query_limit"] = msg.QueryLimit.Value;
        }

        analyticsService.CaptureEvent(msg.CreatedBy?.ToString() ?? "system", "vault", "grant-created", properties);
        return Task.CompletedTask;
    }
}
