using Palladin.Module.Audit.Contracts.Events;
using Palladin.Module.Audit.Contracts.Commands;
using Palladin.Module.Audit.Contracts.ValueObjects;
using Palladin.Module.Audit.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Audit.Triggers.AuditLogExportRequested;

// Bulk data egress is itself a sensitive action, so a requested export is recorded in the audit trail.
// ActorName is denormalized from the requester's JWT display_name claim (Audit keeps no User replica).
// Audit records its OWN action through the same append command as every other module.
[UsedImplicitly]
internal sealed class OnAuditLogExportedDefinition : ConsumerDefinition<OnAuditLogExported>
{
    public OnAuditLogExportedDefinition() => EndpointName = AuditEndpoints.SelfExport;
}

[UsedImplicitly]
internal sealed class OnAuditLogExported(IPublishEndpoint publishEndpoint) : IConsumer<AuditLogExportRequestedEvent>
{
    public Task Consume(ConsumeContext<AuditLogExportRequestedEvent> context)
    {
        var msg = context.Message;
        return publishEndpoint.Publish(new AppendAuditLogCommand(
            OrganizationId: msg.OrganizationId,
            EventType: AuditEventType.ExportRequested,
            ActorType: AuditActorType.User, Result: AuditResult.Succeeded,
            OccurredAt: msg.RequestedAt,
            UserId: msg.RequestedBy, AgentId: null, VaultId: null, EntryId: null, AgentName: null, ActorName: msg.RequestedByName, IpAddress: null,
            Metadata: new Dictionary<string, string>
            {
                ["plan"] = msg.Plan,
                ["filtersUsed"] = msg.FiltersUsed,
                ["jobId"] = msg.JobId.ToString(),
            }), context.CancellationToken);
    }
}
