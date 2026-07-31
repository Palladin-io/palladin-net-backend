using Palladin.Core.Analytics;
using Palladin.Module.Audit.Contracts.Events;
using Palladin.Module.Audit.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Audit.Triggers;

[UsedImplicitly]
internal sealed class OnAuditLogsQueriedDefinition : ConsumerDefinition<OnAuditLogsQueried>
{
    public OnAuditLogsQueriedDefinition() => EndpointName = AuditEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnAuditLogsQueried(IAnalyticsService analyticsService) : IConsumer<AuditLogsQueriedEvent>
{
    public Task Consume(ConsumeContext<AuditLogsQueriedEvent> context)
    {
        var msg = context.Message;
        analyticsService.CaptureEvent(msg.UserId.ToString(), "audit", "log-queried", new Dictionary<string, object>
        {
            ["filters_used"] = msg.FiltersUsed,
            ["organization_id"] = msg.OrganizationId,
        });
        return Task.CompletedTask;
    }
}
