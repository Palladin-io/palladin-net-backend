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
internal sealed class OnAgentDeactivatedDefinition : ConsumerDefinition<OnAgentDeactivated>
{
    public OnAgentDeactivatedDefinition() => EndpointName = AgentsEndpoints.Notification;
}

[UsedImplicitly]
internal sealed class OnAgentDeactivated(
    AgentsDomainReadContext readContext,
    IPublishEndpoint publishEndpoint) : IConsumer<AgentDeactivatedEvent>
{
    public async Task Consume(ConsumeContext<AgentDeactivatedEvent> context)
    {
        var msg = context.Message;
        var ct = context.CancellationToken;

        var agent = await readContext.Agents
            .Where(a => a.Id == msg.AgentId)
            .Select(a => new
            {
                a.Name,
                a.Type,
                a.IconKey,
                a.IconColor,
                a.LastHostname,
                a.LastIp,
                DeactivatorName = a.DeactivatedByUser != null ? a.DeactivatedByUser.DisplayName : null,
            })
            .FirstOrDefaultAsync(ct);

        var metadata = new Dictionary<string, string>
        {
            ["agentId"] = msg.AgentId.ToString(),
            ["agentType"] = agent?.Type ?? string.Empty,
            ["actionType"] = "view_agent",
            ["actionDeepLink"] = $"/agents/{msg.AgentId}",
        };

        if (!string.IsNullOrEmpty(agent?.Name))
        {
            metadata["agentName"] = agent.Name;
        }

        if (!string.IsNullOrEmpty(agent?.IconKey))
        {
            metadata["agentIconKey"] = agent.IconKey;
        }

        if (!string.IsNullOrEmpty(agent?.IconColor))
        {
            metadata["agentIconColor"] = agent.IconColor;
        }

        if (!string.IsNullOrEmpty(agent?.LastHostname))
        {
            metadata["host"] = agent.LastHostname;
        }

        if (!string.IsNullOrEmpty(agent?.LastIp))
        {
            metadata["ip"] = agent.LastIp;
        }

        if (!string.IsNullOrEmpty(agent?.DeactivatorName))
        {
            metadata["actorName"] = agent.DeactivatorName;
        }

        await publishEndpoint.Publish(new BroadcastNotificationCommand(
            OrganizationId: msg.OrganizationId,
            Type: NotificationType.AgentDeactivated,
            Category: NotificationCategory.Update,
            TitleKey: "notification.agent_deactivated.title",
            Metadata: metadata,
            Scopes: [],
            RequiredPermission: Permission.AgentManage,
            SubjectId: msg.AgentId,
            OccurredAt: msg.UpdatedAt,
            CollapsesPending: true), ct);
    }
}
