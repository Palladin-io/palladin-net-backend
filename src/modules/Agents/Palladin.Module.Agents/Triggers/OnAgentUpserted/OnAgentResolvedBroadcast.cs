using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Agents.Contracts.Events;
using Palladin.Module.Agents.Infrastructure.MassTransit;
using Palladin.Module.Agents.Infrastructure.Persistence;
using Palladin.Module.Notification.Contracts.Commands;
using Palladin.Module.Notification.Contracts.ValueObjects;
using JetBrains.Annotations;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Palladin.Module.Agents.Triggers;

[UsedImplicitly]
internal sealed class OnAgentResolvedBroadcastDefinition : ConsumerDefinition<OnAgentResolvedBroadcast>
{
    public OnAgentResolvedBroadcastDefinition() => EndpointName = AgentsEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnAgentResolvedBroadcast(
    AgentsDomainReadContext readContext,
    IPublishEndpoint publishEndpoint) : IConsumer<AgentUpsertedEvent>
{
    public async Task Consume(ConsumeContext<AgentUpsertedEvent> context)
    {
        var msg = context.Message;
        // Deactivation is handled by OnAgentDeactivated (collapses the pending +
        // emits the visible `agent_deactivated` history). Emitting an additional
        // AgentResolved marker here produced a second, empty card — so this
        // consumer now only reacts to approval.
        if (msg.Status == AgentStatus.Active)
        {
            await EmitApprovedAsync(msg, context.CancellationToken);
        }
    }

    private async Task EmitApprovedAsync(AgentUpsertedEvent msg, CancellationToken ct)
    {
        var metadata = new Dictionary<string, string>
        {
            ["agentId"] = msg.AgentId.ToString(),
            ["agentType"] = msg.Type,
            ["actionType"] = "view_agent",
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
            .Select(a => new
            {
                a.LastHostname,
                a.LastIp,
                ApproverName = a.EnrolledByUser != null ? a.EnrolledByUser.DisplayName : null,
            })
            .FirstOrDefaultAsync(ct);
        if (connection?.LastHostname is not null)
        {
            metadata["host"] = connection.LastHostname;
        }

        if (connection?.LastIp is not null)
        {
            metadata["ip"] = connection.LastIp;
        }

        // "By" on the approved card — the user who approved the agent.
        if (!string.IsNullOrEmpty(connection?.ApproverName))
        {
            metadata["actorName"] = connection.ApproverName;
        }

        await publishEndpoint.Publish(new BroadcastNotificationCommand(
            OrganizationId: msg.OrganizationId,
            Type: NotificationType.AgentApproved,
            Category: NotificationCategory.Update,
            TitleKey: "notification.agent_approved.title",
            Metadata: metadata,
            Scopes: [],
            RequiredPermission: Permission.AgentManage,
            SubjectId: msg.AgentId,
            OccurredAt: msg.UpdatedAt,
            CollapsesPending: true), ct);
    }
}
