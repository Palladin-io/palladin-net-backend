using Palladin.Core.Types;
using Palladin.Module.Notification.Contracts.Commands;
using Palladin.Module.Notification.Contracts.ValueObjects;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Vault.Triggers;

[UsedImplicitly]
internal sealed class OnGrantDeniedBroadcastDefinition : ConsumerDefinition<OnGrantDeniedBroadcast>
{
    public OnGrantDeniedBroadcastDefinition() => EndpointName = VaultEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnGrantDeniedBroadcast(
    IPublishEndpoint publishEndpoint) : IConsumer<GrantDeniedEvent>
{
    public async Task Consume(ConsumeContext<GrantDeniedEvent> context)
    {
        var msg = context.Message;
        var ct = context.CancellationToken;

        var metadata = new Dictionary<string, string>
        {
            ["grantId"] = msg.GrantId.ToString(),
            ["vaultId"] = msg.VaultId.ToString(),
            ["agentId"] = msg.AgentId.ToString(),
            ["entryId"] = msg.EntryId.ToString(),
            ["methods"] = msg.Methods.ToString(),
            ["actionType"] = GrantNotificationMapping.ViewGrantActionType,
        };

        await publishEndpoint.Publish(new BroadcastNotificationCommand(
            OrganizationId: msg.OrganizationId,
            Type: NotificationType.GrantDenied,
            Category: NotificationCategory.Update,
            TitleKey: GrantNotificationMapping.GrantDeniedTitleKey,
            Metadata: metadata,
            Scopes: [new NotificationScope(NotificationScopeTypes.User, msg.DeniedBy)],
            RequiredPermission: null,
            SubjectId: msg.GrantId,
            OccurredAt: msg.UpdatedAt,
            CollapsesPending: true), ct);
    }
}
