using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Notification.Contracts.Commands;
using Palladin.Module.Notification.Contracts.ValueObjects;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Vault.Triggers;

[UsedImplicitly]
internal sealed class OnGrantRequestedBroadcastDefinition : ConsumerDefinition<OnGrantRequestedBroadcast>
{
    public OnGrantRequestedBroadcastDefinition() => EndpointName = VaultEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnGrantRequestedBroadcast(
    IPublishEndpoint publishEndpoint) : IConsumer<GrantRequestedEvent>
{
    public async Task Consume(ConsumeContext<GrantRequestedEvent> context)
    {
        var msg = context.Message;
        var ct = context.CancellationToken;

        var metadata = new Dictionary<string, string>
        {
            ["grantId"] = msg.GrantId.ToString(),
            ["vaultId"] = msg.VaultId.ToString(),
            ["agentId"] = msg.AgentId.ToString(),
            ["entryId"] = msg.EntryId.ToString(),
            ["methods"] = msg.RequestedMethods.ToString(),
            ["actionType"] = GrantNotificationMapping.ApproveActionType,
        };

        await publishEndpoint.Publish(new BroadcastNotificationCommand(
            OrganizationId: msg.OrganizationId,
            Type: NotificationType.GrantPending,
            Category: NotificationCategory.ActionRequired,
            TitleKey: GrantNotificationMapping.GrantPendingTitleKey,
            Metadata: metadata,
            Scopes: [new NotificationScope(NotificationScopeTypes.Vault, msg.VaultId)],
            RequiredPermission: Permission.GrantManage,
            SubjectId: msg.GrantId,
            OccurredAt: msg.UpdatedAt,
            Collapsible: true), ct);
    }
}
