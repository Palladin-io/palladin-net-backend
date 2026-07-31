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
internal sealed class OnVaultUpsertedDefinition : ConsumerDefinition<OnVaultUpserted>
{
    public OnVaultUpsertedDefinition() => EndpointName = VaultEndpoints.Self;
}

// Analytics + notification-scope for the vault lifecycle, branching on the change classifier. On
// creation the creator becomes a member (notification vault scope) and — for non-default vaults — the
// vault-created funnel fires; on update only the structural lifecycle event is captured. Encrypted
// metadata and any client-side field classification never enter analytics.
[UsedImplicitly]
internal sealed class OnVaultUpserted(
    IAnalyticsService analyticsService,
    IPublishEndpoint publishEndpoint) : IConsumer<VaultUpsertedEvent>
{
    public async Task Consume(ConsumeContext<VaultUpsertedEvent> context)
    {
        var msg = context.Message;

        if (msg.Change == EntityChange.Created)
        {
            // The onboarding default (personal) vault is auto-created for every new user — counting it
            // would inflate the vault-created funnel. Scope update still runs for it below.
            if (!msg.IsDefault)
            {
                analyticsService.CaptureEvent(msg.UserId.ToString(), "vault", "vault-created");
            }

            await publishEndpoint.Publish(
                new UpdateUserScope(
                    msg.OrganizationId, msg.UserId, NotificationScopeTypes.Vault, msg.VaultId, ScopeAction.Add),
                context.CancellationToken);
            return;
        }

        analyticsService.CaptureEvent(msg.UserId.ToString(), "vault", "vault-updated");
    }
}
