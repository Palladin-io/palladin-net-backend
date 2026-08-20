using System.Globalization;
using JetBrains.Annotations;
using MassTransit;
using Palladin.Module.Notification.Contracts.Commands;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Infrastructure.MassTransit;

namespace Palladin.Module.Vault.Triggers;

[UsedImplicitly]
internal sealed class OnVaultSyncInvalidatedDefinition : ConsumerDefinition<OnVaultSyncInvalidated>
{
    public OnVaultSyncInvalidatedDefinition() => EndpointName = VaultEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnVaultSyncInvalidated(IPublishEndpoint publishEndpoint)
    : IConsumer<VaultSyncInvalidatedEvent>
{
    public Task Consume(ConsumeContext<VaultSyncInvalidatedEvent> context)
    {
        var msg = context.Message;
        return publishEndpoint.Publish(
            new BroadcastVaultSyncInvalidationCommand(
                msg.OrganizationId,
                msg.VaultId,
                msg.MemberSequence.ToString(CultureInfo.InvariantCulture),
                msg.MutationVersion.ToString(CultureInfo.InvariantCulture),
                false,
                msg.MemberUserIds,
                msg.OccurredAt),
            context.CancellationToken);
    }
}
