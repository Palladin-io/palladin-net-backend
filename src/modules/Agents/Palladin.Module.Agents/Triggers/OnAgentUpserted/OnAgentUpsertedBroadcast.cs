using Palladin.Core.Security;
using Palladin.Module.Agents.Contracts.Events;
using Palladin.Module.Agents.Infrastructure.MassTransit;
using Palladin.Module.Agents.Infrastructure.Persistence;
using Palladin.Module.Notification.Contracts.Commands;
using Palladin.Module.Notification.Contracts.ValueObjects;
using Palladin.Core.Types;
using JetBrains.Annotations;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Palladin.Module.Agents.Triggers;

[UsedImplicitly]
internal sealed class OnAgentUpsertedBroadcastDefinition : ConsumerDefinition<OnAgentUpsertedBroadcast>
{
    public OnAgentUpsertedBroadcastDefinition() => EndpointName = AgentsEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnAgentUpsertedBroadcast(
    AgentsDomainReadContext readContext,
    IPublishEndpoint publishEndpoint) : IConsumer<AgentUpsertedEvent>
{
    public async Task Consume(ConsumeContext<AgentUpsertedEvent> context)
    {
        var msg = context.Message;
        var ct = context.CancellationToken;
        if (msg.Status != AgentStatus.Pending)
        {
            return;
        }

        var metadata = new Dictionary<string, string>
        {
            ["agentId"] = msg.AgentId.ToString(),
            ["agentType"] = msg.Type,
            ["actionType"] = "approve_agent",
            ["actionDeepLink"] = $"/agents/{msg.AgentId}",
        };
        if (msg.Name is not null)
        {
            metadata["agentName"] = msg.Name;
        }
        if (!string.IsNullOrWhiteSpace(msg.PublicKey))
        {
            metadata["agentPublicKey"] = msg.PublicKey;
        }

        if (!string.IsNullOrEmpty(msg.IconKey))
        {
            metadata["agentIconKey"] = msg.IconKey;
        }

        if (!string.IsNullOrEmpty(msg.IconColor))
        {
            metadata["agentIconColor"] = msg.IconColor;
        }

        var connection = await readContext.Agents
            .Where(a => a.Id == msg.AgentId)
            .Select(a => new { a.LastHostname, a.LastIp, a.LastUsedApiKeyId })
            .FirstOrDefaultAsync(ct);
        if (connection?.LastHostname is not null)
        {
            metadata["host"] = connection.LastHostname;
        }

        if (connection?.LastIp is not null)
        {
            metadata["ip"] = connection.LastIp;
        }

        // Enrollment key reference — masked suffix only, NEVER the plaintext key.
        // Lets the panel deep-link an unexpected agent's deny warning straight to
        // the (possibly leaked) key in API Keys, shown as `pl_••••{suffix}`.
        if (connection?.LastUsedApiKeyId is { } apiKeyId)
        {
            var key = await readContext.ApiKeys
                .Where(k => k.Id == apiKeyId)
                .Select(k => new { k.Id, k.KeySuffix })
                .FirstOrDefaultAsync(ct);
            if (key is not null)
            {
                metadata["apiKeyId"] = key.Id.ToString();
                metadata["apiKeySuffix"] = key.KeySuffix;
            }
        }

        await publishEndpoint.Publish(new BroadcastNotificationCommand(
            OrganizationId: msg.OrganizationId,
            Type: NotificationType.AgentPending,
            Category: NotificationCategory.ActionRequired,
            TitleKey: "notification.agent_pending.title",
            Metadata: metadata,
            Scopes: [],
            RequiredPermission: Permission.AgentManage,
            SubjectId: msg.AgentId,
            OccurredAt: msg.UpdatedAt,
            Collapsible: true), ct);
    }
}
