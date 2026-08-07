using Palladin.Core.Analytics;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Vault.Triggers;

[UsedImplicitly]
internal sealed class OnCredentialAccessDeniedDefinition : ConsumerDefinition<OnCredentialAccessDenied>
{
    public OnCredentialAccessDeniedDefinition() => EndpointName = VaultEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnCredentialAccessDenied(IAnalyticsService analyticsService) : IConsumer<CredentialAccessDeniedEvent>
{
    public Task Consume(ConsumeContext<CredentialAccessDeniedEvent> context)
    {
        var msg = context.Message;
        analyticsService.CaptureEvent(msg.AgentId.ToString(), "vault", "credential-access-denied", new Dictionary<string, object>
        {
            ["vault_id"] = msg.VaultId,
            ["entry_id"] = msg.EntryId,
            ["reason"] = msg.Reason,
        });
        return Task.CompletedTask;
    }
}
