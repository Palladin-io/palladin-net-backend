using Palladin.Core.Analytics;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Vault.Triggers;

[UsedImplicitly]
internal sealed class OnEntryDiscoveryRequestedDefinition : ConsumerDefinition<OnEntryDiscoveryRequested>
{
    public OnEntryDiscoveryRequestedDefinition() => EndpointName = VaultEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnEntryDiscoveryRequested(IAnalyticsService analyticsService) : IConsumer<EntryDiscoveryRequestedEvent>
{
    public Task Consume(ConsumeContext<EntryDiscoveryRequestedEvent> context)
    {
        var msg = context.Message;
        analyticsService.CaptureEvent(msg.AgentId.ToString(), "vault", "entry-discovery", new Dictionary<string, object>
        {
            ["result_count"] = msg.ResultCount,
            ["organization_id"] = msg.OrganizationId,
        });
        return Task.CompletedTask;
    }
}
