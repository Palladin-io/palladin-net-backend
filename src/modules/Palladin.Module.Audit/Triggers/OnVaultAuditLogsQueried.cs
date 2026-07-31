using Palladin.Core.Analytics;
using Palladin.Module.Audit.Contracts.Events;
using Palladin.Module.Audit.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Audit.Triggers;

[UsedImplicitly]
internal sealed class OnVaultAuditLogsQueriedDefinition : ConsumerDefinition<OnVaultAuditLogsQueried>
{
    public OnVaultAuditLogsQueriedDefinition() => EndpointName = AuditEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnVaultAuditLogsQueried(IAnalyticsService analyticsService) : IConsumer<VaultAuditLogsQueriedEvent>
{
    public Task Consume(ConsumeContext<VaultAuditLogsQueriedEvent> context)
    {
        var msg = context.Message;
        analyticsService.CaptureEvent(msg.UserId.ToString(), "audit", "vault-log-queried", new Dictionary<string, object>
        {
            ["filters_used"] = msg.FiltersUsed,
            ["result_count"] = msg.ResultCount,
            ["vault_id"] = msg.VaultId,
        });
        return Task.CompletedTask;
    }
}
