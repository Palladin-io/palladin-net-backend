using Palladin.Core.Analytics;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Vault.Triggers;

[UsedImplicitly]
internal sealed class OnEntriesImportedDefinition : ConsumerDefinition<OnEntriesImported>
{
    public OnEntriesImportedDefinition() => EndpointName = VaultEndpoints.Self;
}

// Bulk-import analytics. Per-entry events still fire for audit/search/onboarding; this records the batch.
[UsedImplicitly]
internal sealed class OnEntriesImported(IAnalyticsService analyticsService) : IConsumer<EntriesImportedEvent>
{
    public Task Consume(ConsumeContext<EntriesImportedEvent> context)
    {
        var msg = context.Message;
        analyticsService.CaptureEvent(msg.UserId.ToString(), "vault", "entries-imported", new Dictionary<string, object>
        {
            ["vault_id"] = msg.VaultId,
            ["count"] = msg.Count,
            ["format"] = msg.Format,
        });
        return Task.CompletedTask;
    }
}
