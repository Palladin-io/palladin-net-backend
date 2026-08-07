using Palladin.Core.Types;
using Palladin.Module.Notification.Contracts.Commands;
using Palladin.Module.Notification.Contracts.ValueObjects;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;
using NodaTime.Text;

namespace Palladin.Module.Vault.Triggers;

[UsedImplicitly]
internal sealed class OnGrantApprovedBroadcastDefinition : ConsumerDefinition<OnGrantApprovedBroadcast>
{
    public OnGrantApprovedBroadcastDefinition() => EndpointName = VaultEndpoints.Notification;
}

[UsedImplicitly]
internal sealed class OnGrantApprovedBroadcast(
    IPublishEndpoint publishEndpoint) : IConsumer<GrantApprovedEvent>
{
    public async Task Consume(ConsumeContext<GrantApprovedEvent> context)
    {
        var msg = context.Message;
        var ct = context.CancellationToken;

        var metadata = new Dictionary<string, string>
        {
            ["grantId"] = msg.GrantId.ToString(),
            ["vaultId"] = msg.VaultId.ToString(),
            ["agentId"] = msg.AgentId.ToString(),
            ["grantType"] = GrantNotificationMapping.GrantTypeWire(msg.Type),
            ["methods"] = msg.Methods.ToString(),
            ["actionType"] = GrantNotificationMapping.ViewGrantActionType,
        };
        if (msg.EntryId is not null)
        {
            metadata["entryId"] = msg.EntryId.Value.ToString();
        }
        if (msg.QueryLimit is not null)
        {
            metadata["queryLimit"] = msg.QueryLimit.Value.ToString();
            metadata["queryCount"] = "0";
        }
        if (msg.ExpiresAt is not null)
        {
            metadata["expiresAt"] = InstantPattern.ExtendedIso.Format(msg.ExpiresAt.Value);
        }

        await publishEndpoint.Publish(new BroadcastNotificationCommand(
            OrganizationId: msg.OrganizationId,
            Type: NotificationType.GrantApproved,
            Category: NotificationCategory.Update,
            TitleKey: GrantNotificationMapping.GrantApprovedTitleKey,
            Metadata: metadata,
            Scopes: [new NotificationScope(NotificationScopeTypes.User, msg.ApprovedBy)],
            RequiredPermission: null,
            SubjectId: msg.GrantId,
            OccurredAt: msg.UpdatedAt,
            CollapsesPending: true), ct);
    }
}
