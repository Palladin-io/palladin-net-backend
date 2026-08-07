using Palladin.Module.Agents.Contracts.Events;
using Palladin.Module.Agents.Infrastructure.MassTransit;
using Palladin.Module.Audit.Contracts.Commands;
using Palladin.Module.Audit.Contracts.ValueObjects;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Agents.Triggers;

[UsedImplicitly]
internal sealed class OnApiKeyRevokedAuditDefinition : ConsumerDefinition<OnApiKeyRevokedAudit>
{
    public OnApiKeyRevokedAuditDefinition() => EndpointName = AgentsEndpoints.Audit;
}

[UsedImplicitly]
internal sealed class OnApiKeyRevokedAudit(IPublishEndpoint publishEndpoint) : IConsumer<ApiKeyRevokedEvent>
{
    public Task Consume(ConsumeContext<ApiKeyRevokedEvent> context)
    {
        var msg = context.Message;
        return publishEndpoint.Publish(new AppendAuditLogCommand(
            OrganizationId: msg.OrganizationId,
            EventType: AuditEventType.ApiKeyRevoked,
            ActorType: AuditActorType.User, Result: AuditResult.Succeeded,
            OccurredAt: msg.UpdatedAt,
            UserId: msg.RevokedBy, AgentId: null, VaultId: null, EntryId: null, AgentName: null, ActorName: msg.RevokedByName, IpAddress: null,
            Metadata: new Dictionary<string, string>
            {
                ["keyId"] = msg.ApiKeyId.ToString(),
                ["keyName"] = msg.Name,
                ["keySuffix"] = msg.KeySuffix,
            }), context.CancellationToken);
    }
}
