using Palladin.Core.Analytics;
using Palladin.Core.Types;
using Palladin.Module.Notification.Contracts.Commands;
using Palladin.Module.Notification.Contracts.ValueObjects;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Vault.Triggers;

[UsedImplicitly]
internal sealed class OnVaultDeletedDefinition : ConsumerDefinition<OnVaultDeleted>
{
    public OnVaultDeletedDefinition() => EndpointName = VaultEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnVaultDeleted(
    IAnalyticsService analyticsService,
    IPublishEndpoint publishEndpoint) : IConsumer<VaultDeletedEvent>
{
    public async Task Consume(ConsumeContext<VaultDeletedEvent> context)
    {
        var msg = context.Message;

        analyticsService.CaptureEvent(msg.UserId.ToString(), "vault", "vault-deleted", new Dictionary<string, object>
        {
            ["member_count"] = msg.MemberCount,
        });

        foreach (var memberUserId in msg.MemberUserIds)
        {
            await publishEndpoint.Publish(
                new UpdateUserScope(msg.OrganizationId, memberUserId, NotificationScopeTypes.Vault, msg.VaultId, ScopeAction.Delete),
                context.CancellationToken);
        }
    }
}
