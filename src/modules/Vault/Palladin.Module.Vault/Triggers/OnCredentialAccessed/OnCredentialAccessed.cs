using Palladin.Core.Analytics;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Vault.Triggers;

[UsedImplicitly]
internal sealed class OnCredentialAccessedDefinition : ConsumerDefinition<OnCredentialAccessed>
{
    public OnCredentialAccessedDefinition() => EndpointName = VaultEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnCredentialAccessed(IAnalyticsService analyticsService) : IConsumer<CredentialAccessedEvent>
{
    public Task Consume(ConsumeContext<CredentialAccessedEvent> context)
    {
        var msg = context.Message;
        var properties = new Dictionary<string, object>
        {
            ["vault_id"] = msg.VaultId,
            ["entry_id"] = msg.EntryId,
            ["grant_type"] = msg.Type.ToString(),
            ["method"] = msg.Method.ToString(),
        };

        if (msg.RemainingUses is not null)
        {
            properties["remaining_uses"] = msg.RemainingUses.Value;
        }

        analyticsService.CaptureEvent(msg.AgentId.ToString(), "vault", "credential-accessed", properties);
        return Task.CompletedTask;
    }
}
