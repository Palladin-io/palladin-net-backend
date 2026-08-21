using System.Globalization;
using JetBrains.Annotations;
using MassTransit;
using Palladin.Module.Notification.Contracts.Commands;
using Palladin.Module.Notification.Infrastructure.MassTransit;
using Palladin.Module.Notification.Infrastructure.SignalR;
using Palladin.Module.Notification.Shared;

namespace Palladin.Module.Notification.Features;

[UsedImplicitly]
internal sealed class BroadcastVaultSyncInvalidationConsumerDefinition
    : ConsumerDefinition<BroadcastVaultSyncInvalidationConsumer>
{
    public BroadcastVaultSyncInvalidationConsumerDefinition() => EndpointName = NotificationEndpoints.Realtime;
}

[UsedImplicitly]
internal sealed class BroadcastVaultSyncInvalidationConsumer(
    IRealtimeEventNotifier realtimeNotifier) : IConsumer<BroadcastVaultSyncInvalidationCommand>
{
    public async Task Consume(ConsumeContext<BroadcastVaultSyncInvalidationCommand> context)
    {
        var msg = context.Message;
        Validate(msg);

        await realtimeNotifier.NotifyVaultSyncInvalidationAsync(
            msg.RecipientUserIds.Distinct().ToArray(),
            new VaultSyncInvalidationPayload(
                1,
                msg.VaultId,
                msg.MemberSequence,
                msg.MutationVersion,
                msg.Removed),
            context.CancellationToken);
    }

    private static void Validate(BroadcastVaultSyncInvalidationCommand msg)
    {
        if (msg.OrganizationId == Guid.Empty
            || msg.VaultId == Guid.Empty
            || !IsCanonicalUInt64(msg.MemberSequence)
            || !IsCanonicalUInt64(msg.MutationVersion)
            || msg.RecipientUserIds.Any(id => id == Guid.Empty)
            || msg.RecipientUserIds.Count == 0)
        {
            throw new InvalidOperationException("Vault sync invalidation is malformed.");
        }
    }

    private static bool IsCanonicalUInt64(string value) =>
        ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
        && parsed.ToString(CultureInfo.InvariantCulture) == value;
}
