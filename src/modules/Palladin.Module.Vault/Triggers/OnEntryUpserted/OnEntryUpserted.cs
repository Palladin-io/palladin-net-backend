using Palladin.Core.Analytics;
using Palladin.Core.Types;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Vault.Triggers;

[UsedImplicitly]
internal sealed class OnEntryUpsertedDefinition : ConsumerDefinition<OnEntryUpserted>
{
    public OnEntryUpsertedDefinition() => EndpointName = VaultEndpoints.Self;
}

// Entry lifecycle analytics, branching on the change classifier.
[UsedImplicitly]
internal sealed class OnEntryUpserted(IAnalyticsService analyticsService) : IConsumer<EntryUpsertedEvent>
{
    public Task Consume(ConsumeContext<EntryUpsertedEvent> context)
    {
        var msg = context.Message;

        if (msg.Change == EntityChange.Created)
        {
            // Bulk-imported entries are counted once by the batch entries-imported event; skip per-entry
            // analytics here so a 500-item import does not fire 500 entry-created captures.
            if (msg.ViaImport)
            {
                return Task.CompletedTask;
            }

            analyticsService.CaptureEvent(msg.UserId.ToString(), "vault", "entry-created", new Dictionary<string, object>
            {
                ["vault_id"] = msg.VaultId,
                ["entry_id"] = msg.EntryId,
                ["revision"] = msg.Revision,
            });
            return Task.CompletedTask;
        }

        analyticsService.CaptureEvent(msg.UserId.ToString(), "vault", "entry-updated", new Dictionary<string, object>
        {
            ["vault_id"] = msg.VaultId,
            ["entry_id"] = msg.EntryId,
            ["revision"] = msg.Revision,
        });
        return Task.CompletedTask;
    }
}
