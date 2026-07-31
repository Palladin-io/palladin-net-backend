using Palladin.Module.Agents.Contracts.Events;
using Palladin.Module.Agents.Infrastructure.MassTransit;
using Palladin.Module.Audit.Contracts.Commands;
using Palladin.Module.Audit.Contracts.ValueObjects;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Agents.Triggers;

[UsedImplicitly]
internal sealed class OnApiKeyActivatedAuditDefinition : ConsumerDefinition<OnApiKeyActivatedAudit>
{
    public OnApiKeyActivatedAuditDefinition() => EndpointName = AgentsEndpoints.Audit;
}

[UsedImplicitly]
internal sealed class OnApiKeyActivatedAudit(IPublishEndpoint publishEndpoint) : IConsumer<ApiKeyActivatedEvent>
{
    public Task Consume(ConsumeContext<ApiKeyActivatedEvent> context)
    {
        var msg = context.Message;
        return publishEndpoint.Publish(new AppendAuditLogCommand(
            OrganizationId: msg.OrganizationId,
            EventType: AuditEventType.ApiKeyActivated,
            ActorType: AuditActorType.User, Result: AuditResult.Succeeded,
            OccurredAt: msg.UpdatedAt,
            UserId: msg.ActivatedBy, AgentId: null, VaultId: null, EntryId: null, AgentName: null, ActorName: msg.ActivatedByName, IpAddress: null,
            Metadata: new Dictionary<string, string>
            {
                ["keyId"] = msg.ApiKeyId.ToString(),
                ["keyName"] = msg.Name,
                ["keySuffix"] = msg.KeySuffix,
            }), context.CancellationToken);
    }
}
