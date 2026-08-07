using Palladin.Core.Analytics;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Vault.Triggers;

[UsedImplicitly]
internal sealed class OnEntryDeletedDefinition : ConsumerDefinition<OnEntryDeleted>
{
    public OnEntryDeletedDefinition() => EndpointName = VaultEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnEntryDeleted(IAnalyticsService analyticsService) : IConsumer<EntryDeletedEvent>
{
    public Task Consume(ConsumeContext<EntryDeletedEvent> context)
    {
        var msg = context.Message;
        analyticsService.CaptureEvent(msg.DeletedBy.ToString(), "vault", "entry-deleted", new Dictionary<string, object>
        {
            ["vault_id"] = msg.VaultId,
            ["entry_id"] = msg.EntryId,
        });
        return Task.CompletedTask;
    }
}
