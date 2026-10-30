using JetBrains.Annotations;
using MassTransit;
using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Notification.Contracts.Commands;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Infrastructure.MassTransit;

namespace Palladin.Module.Vault.Triggers;

[UsedImplicitly]
internal sealed class OnAgentSubmissionChangedNotificationDefinition
    : ConsumerDefinition<OnAgentSubmissionChangedNotification>
{
    public OnAgentSubmissionChangedNotificationDefinition() => EndpointName = VaultEndpoints.Self;
}

// Inbox and push carry only the opaque submission reference; approvers resolve everything else after unlock.
[UsedImplicitly]
internal sealed class OnAgentSubmissionChangedNotification(IPublishEndpoint publishEndpoint)
    : IConsumer<AgentSubmissionChangedEvent>
{
    internal const string PendingTitleKey = "notification.submission_pending.title";

    public Task Consume(ConsumeContext<AgentSubmissionChangedEvent> context)
    {
        var msg = context.Message;
        var metadata = new Dictionary<string, string> { ["submissionId"] = msg.SubmissionId.ToString() };
        return msg.Action switch
        {
            AgentSubmissionAction.Submitted or AgentSubmissionAction.Restored => publishEndpoint.Publish(
                new BroadcastNotificationCommand(
                    OrganizationId: msg.OrganizationId,
                    Type: NotificationType.SubmissionPending,
                    Category: NotificationCategory.ActionRequired,
                    TitleKey: PendingTitleKey,
                    Metadata: metadata,
                    Scopes: [],
                    RequiredPermission: Permission.GrantManage,
                    SubjectId: msg.SubmissionId,
                    OccurredAt: msg.OccurredAt,
                    Collapsible: true),
                context.CancellationToken),
            AgentSubmissionAction.Approved or AgentSubmissionAction.Denied or AgentSubmissionAction.Purged
                => publishEndpoint.Publish(
                    new BroadcastNotificationCommand(
                        OrganizationId: msg.OrganizationId,
                        Type: NotificationType.SubmissionResolved,
                        Category: NotificationCategory.Update,
                        TitleKey: PendingTitleKey,
                        Metadata: metadata,
                        Scopes: [],
                        RequiredPermission: Permission.GrantManage,
                        SubjectId: msg.SubmissionId,
                        OccurredAt: msg.OccurredAt,
                        CollapsesPending: true),
                    context.CancellationToken),
            _ => Task.CompletedTask,
        };
    }
}
