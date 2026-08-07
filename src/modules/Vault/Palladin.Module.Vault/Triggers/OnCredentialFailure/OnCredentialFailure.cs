using Palladin.Core.Analytics;
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
internal sealed class OnCredentialFailureReportedDefinition : ConsumerDefinition<OnCredentialFailureReported>
{
    public OnCredentialFailureReportedDefinition() => EndpointName = VaultEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnCredentialFailureReported(IAnalyticsService analyticsService)
    : IConsumer<CredentialFailureReportedEvent>
{
    public Task Consume(ConsumeContext<CredentialFailureReportedEvent> context)
    {
        var msg = context.Message;
        analyticsService.CaptureEvent(msg.AgentId.ToString(), "vault", "credential-failure-reported", new Dictionary<string, object>
        {
            ["organization_id"] = msg.OrganizationId,
            ["vault_id"] = msg.VaultId,
            ["entry_id"] = msg.EntryId,
            ["code"] = msg.Code,
        });
        return Task.CompletedTask;
    }
}

[UsedImplicitly]
internal sealed class OnCredentialFailureReportedBroadcastDefinition : ConsumerDefinition<OnCredentialFailureReportedBroadcast>
{
    public OnCredentialFailureReportedBroadcastDefinition() => EndpointName = VaultEndpoints.Notification;
}

[UsedImplicitly]
internal sealed class OnCredentialFailureReportedBroadcast(
    IPublishEndpoint publishEndpoint)
    : IConsumer<CredentialFailureReportedEvent>
{
    public async Task Consume(ConsumeContext<CredentialFailureReportedEvent> context)
    {
        var msg = context.Message;
        var ct = context.CancellationToken;
        var errorHint = GrantNotificationMapping.CredentialFailureErrorHint(msg.Code);

        var metadata = new Dictionary<string, string>
        {
            ["entryId"] = msg.EntryId.ToString(),
            ["vaultId"] = msg.VaultId.ToString(),
            ["agentId"] = msg.AgentId.ToString(),
            ["errorHint"] = errorHint,
            ["actionType"] = GrantNotificationMapping.ViewEntryActionType,
        };

        await publishEndpoint.Publish(new BroadcastNotificationCommand(
            OrganizationId: msg.OrganizationId,
            Type: NotificationType.CredentialStale,
            Category: NotificationCategory.Update,
            TitleKey: GrantNotificationMapping.CredentialStaleTitleKey,
            Metadata: metadata,
            Scopes: [new NotificationScope(NotificationScopeTypes.Vault, msg.VaultId)],
            RequiredPermission: Permission.GrantManage,
            SubjectId: msg.EntryId,
            OccurredAt: msg.UpdatedAt), ct);
    }
}
