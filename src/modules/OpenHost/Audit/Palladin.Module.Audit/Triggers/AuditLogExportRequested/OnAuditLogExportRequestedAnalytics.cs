using Palladin.Core.Analytics;
using Palladin.Module.Audit.Contracts.Events;
using Palladin.Module.Audit.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Audit.Triggers.AuditLogExportRequested;

[UsedImplicitly]
internal sealed class OnAuditLogExportRequestedAnalyticsDefinition : ConsumerDefinition<OnAuditLogExportRequestedAnalytics>
{
    public OnAuditLogExportRequestedAnalyticsDefinition() => EndpointName = AuditEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnAuditLogExportRequestedAnalytics(IAnalyticsService analyticsService)
    : IConsumer<AuditLogExportRequestedEvent>
{
    public Task Consume(ConsumeContext<AuditLogExportRequestedEvent> context)
    {
        var msg = context.Message;
        analyticsService.CaptureEvent(msg.RequestedBy.ToString(), "audit", "export-requested", new Dictionary<string, object>
        {
            ["plan"] = msg.Plan,
            ["filters_used"] = msg.FiltersUsed,
            ["organization_id"] = msg.OrganizationId,
        });
        return Task.CompletedTask;
    }
}
