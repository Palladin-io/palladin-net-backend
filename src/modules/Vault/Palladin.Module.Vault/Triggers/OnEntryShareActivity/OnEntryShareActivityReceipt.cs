using JetBrains.Annotations;
using MassTransit;
using Palladin.Core.Types;
using Palladin.Module.Notification.Contracts.Commands;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Infrastructure.MassTransit;

namespace Palladin.Module.Vault.Triggers;

[UsedImplicitly]
internal sealed class OnEntryShareActivityReceiptDefinition : ConsumerDefinition<OnEntryShareActivityReceipt>
{
    public OnEntryShareActivityReceiptDefinition() => EndpointName = VaultEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnEntryShareActivityReceipt(IPublishEndpoint publishEndpoint)
    : IConsumer<EntryShareActivityEvent>
{
    public Task Consume(ConsumeContext<EntryShareActivityEvent> context)
    {
        var message = context.Message;
        return message.Kind == EntryShareActivityKind.Confirmed && message.NotifySender
            ? publishEndpoint.Publish(new RecordEntryShareReceiptCommand(message.OrganizationId, message.SenderId,
                message.ShareId, message.VaultId, message.EntryId, message.UpdatedAt), context.CancellationToken)
            : Task.CompletedTask;
    }
}
