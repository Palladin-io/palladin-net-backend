using Palladin.Core.Analytics;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Vault.Triggers;

[UsedImplicitly]
internal sealed class OnVaultExportedDefinition : ConsumerDefinition<OnVaultExported>
{
    public OnVaultExportedDefinition() => EndpointName = VaultEndpoints.Self;
}

// Client-side vault export analytics.
[UsedImplicitly]
internal sealed class OnVaultExported(IAnalyticsService analyticsService) : IConsumer<VaultExportedEvent>
{
    public Task Consume(ConsumeContext<VaultExportedEvent> context)
    {
        var msg = context.Message;
        analyticsService.CaptureEvent(msg.UserId.ToString(), "vault", "vault-exported", new Dictionary<string, object>
        {
            ["vault_id"] = msg.VaultId,
            ["count"] = msg.EntryCount,
            ["format"] = msg.Format,
        });
        return Task.CompletedTask;
    }
}
