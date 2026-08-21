using System.Globalization;
using JetBrains.Annotations;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Palladin.Module.Notification.Contracts.Commands;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Infrastructure.MassTransit;
using Palladin.Module.Vault.Infrastructure.Persistence;

namespace Palladin.Module.Vault.Triggers;

[UsedImplicitly]
internal sealed class OnVaultSyncInvalidatedDefinition : ConsumerDefinition<OnVaultSyncInvalidated>
{
    public OnVaultSyncInvalidatedDefinition() => EndpointName = VaultEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnVaultSyncInvalidated(
    IPublishEndpoint publishEndpoint,
    VaultDomainReadContext readContext)
    : IConsumer<VaultSyncInvalidatedEvent>
{
    public async Task Consume(ConsumeContext<VaultSyncInvalidatedEvent> context)
    {
        var msg = context.Message;
        var recipientUserIds = await readContext.VaultMembers
            .AsNoTracking()
            .Where(member => member.OrganizationId == msg.OrganizationId && member.VaultId == msg.VaultId)
            .Select(member => member.UserId)
            .Distinct()
            .ToArrayAsync(context.CancellationToken);
        if (recipientUserIds.Length == 0)
        {
            return;
        }

        await publishEndpoint.Publish(
            new BroadcastVaultSyncInvalidationCommand(
                msg.OrganizationId,
                msg.VaultId,
                msg.MemberSequence.ToString(CultureInfo.InvariantCulture),
                msg.MutationVersion.ToString(CultureInfo.InvariantCulture),
                false,
                recipientUserIds,
                msg.OccurredAt),
            context.CancellationToken);
    }
}
