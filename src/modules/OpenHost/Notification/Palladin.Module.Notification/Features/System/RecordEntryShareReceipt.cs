using JetBrains.Annotations;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Palladin.Core.Types;
using Palladin.Module.Notification.Contracts.Commands;
using Palladin.Module.Notification.Domain;
using Palladin.Module.Notification.Infrastructure.MassTransit;
using Palladin.Module.Notification.Infrastructure.Persistence;

namespace Palladin.Module.Notification.Features;

[UsedImplicitly]
internal sealed class RecordEntryShareReceiptConsumerDefinition : ConsumerDefinition<RecordEntryShareReceiptConsumer>
{
    public RecordEntryShareReceiptConsumerDefinition() => EndpointName = NotificationEndpoints.Inbox;
}

[UsedImplicitly]
internal sealed class RecordEntryShareReceiptConsumer(NotificationDomainWriteContext domainWriteContext)
    : IConsumer<RecordEntryShareReceiptCommand>
{
    private const string TitleKey = "notification.entry_share_received.title";

    public async Task Consume(ConsumeContext<RecordEntryShareReceiptCommand> context)
    {
        var message = context.Message;
        if (message.OrganizationId == Guid.Empty || message.SenderId == Guid.Empty
            || message.ShareId == Guid.Empty || message.VaultId == Guid.Empty || message.EntryId == Guid.Empty)
        {
            throw new ArgumentException("Sharing receipts require complete structural scope.");
        }

        var existing = await domainWriteContext.InboxItems.AnyAsync(x =>
            x.OrganizationId == message.OrganizationId && x.UserId == message.SenderId
            && x.Id == message.ShareId, context.CancellationToken);
        if (existing)
        {
            return;
        }

        domainWriteContext.Add(InboxItem.Create(message.ShareId, message.OrganizationId, message.SenderId,
            NotificationType.EntryShareReceived, NotificationCategory.Update,
            TitleKey,
            new Dictionary<string, string>
            {
                ["shareId"] = message.ShareId.ToString("D"),
                ["vaultId"] = message.VaultId.ToString("D"),
                ["entryId"] = message.EntryId.ToString("D"),
            }, string.Empty, Guid.Empty, message.ShareId, false, null, message.OccurredAt));
        await domainWriteContext.CommitAsync(context.CancellationToken);
    }
}
