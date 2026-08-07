using Palladin.Module.Agents.Contracts.Events;
using Palladin.Module.Agents.Infrastructure.MassTransit;
using Palladin.Module.Audit.Contracts.Commands;
using Palladin.Module.Audit.Contracts.ValueObjects;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Agents.Triggers;

[UsedImplicitly]
internal sealed class OnApiKeyCreatedAuditDefinition : ConsumerDefinition<OnApiKeyCreatedAudit>
{
    public OnApiKeyCreatedAuditDefinition() => EndpointName = AgentsEndpoints.Audit;
}

[UsedImplicitly]
internal sealed class OnApiKeyCreatedAudit(IPublishEndpoint publishEndpoint) : IConsumer<ApiKeyCreatedEvent>
{
    public Task Consume(ConsumeContext<ApiKeyCreatedEvent> context)
    {
        var msg = context.Message;
        return publishEndpoint.Publish(new AppendAuditLogCommand(
            OrganizationId: msg.OrganizationId,
            EventType: AuditEventType.ApiKeyCreated,
            ActorType: AuditActorType.User, Result: AuditResult.Succeeded,
            OccurredAt: msg.CreatedAt,
            UserId: msg.CreatedBy, AgentId: null, VaultId: null, EntryId: null, AgentName: null, ActorName: msg.CreatedByName, IpAddress: null,
            Metadata: new Dictionary<string, string>
            {
                ["keyId"] = msg.ApiKeyId.ToString(),
                ["keyName"] = msg.Name,
                ["keySuffix"] = msg.KeySuffix,
            }), context.CancellationToken);
    }
}
