using Palladin.Module.Agents.Contracts.Events;
using Palladin.Module.Agents.Infrastructure.MassTransit;
using Palladin.Module.Audit.Contracts.Commands;
using Palladin.Module.Audit.Contracts.ValueObjects;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Agents.Triggers;

[UsedImplicitly]
internal sealed class OnApiKeyDeletedAuditDefinition : ConsumerDefinition<OnApiKeyDeletedAudit>
{
    public OnApiKeyDeletedAuditDefinition() => EndpointName = AgentsEndpoints.Audit;
}

[UsedImplicitly]
internal sealed class OnApiKeyDeletedAudit(IPublishEndpoint publishEndpoint) : IConsumer<ApiKeyDeletedEvent>
{
    public Task Consume(ConsumeContext<ApiKeyDeletedEvent> context)
    {
        var msg = context.Message;
        return publishEndpoint.Publish(new AppendAuditLogCommand(
            OrganizationId: msg.OrganizationId,
            EventType: AuditEventType.ApiKeyDeleted,
            ActorType: AuditActorType.User, Result: AuditResult.Succeeded,
            OccurredAt: msg.DeletedAt,
            UserId: msg.DeletedBy, AgentId: null, VaultId: null, EntryId: null, AgentName: null, ActorName: msg.DeletedByName, IpAddress: null,
            Metadata: new Dictionary<string, string>
            {
                ["keyId"] = msg.ApiKeyId.ToString(),
                ["keyName"] = msg.Name,
                ["keySuffix"] = msg.KeySuffix,
            }), context.CancellationToken);
    }
}
