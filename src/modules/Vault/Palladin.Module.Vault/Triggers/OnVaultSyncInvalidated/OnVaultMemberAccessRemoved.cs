using System.Globalization;
using JetBrains.Annotations;
using MassTransit;
using Palladin.Module.Notification.Contracts.Commands;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Infrastructure.MassTransit;

namespace Palladin.Module.Vault.Triggers;

[UsedImplicitly]
internal sealed class OnVaultMemberAccessRemovedDefinition : ConsumerDefinition<OnVaultMemberAccessRemoved>
{
    public OnVaultMemberAccessRemovedDefinition() => EndpointName = VaultEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnVaultMemberAccessRemoved(IPublishEndpoint publishEndpoint)
    : IConsumer<VaultMemberAccessRemovedEvent>
{
    public Task Consume(ConsumeContext<VaultMemberAccessRemovedEvent> context)
    {
        var msg = context.Message;
        return publishEndpoint.Publish(
            new BroadcastVaultSyncInvalidationCommand(
                msg.OrganizationId,
                msg.VaultId,
                msg.MemberSequence.ToString(CultureInfo.InvariantCulture),
                msg.MutationVersion.ToString(CultureInfo.InvariantCulture),
                true,
                [msg.UserId],
                msg.OccurredAt),
            context.CancellationToken);
    }
}
