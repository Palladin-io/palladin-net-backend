using Palladin.Module.Search.Domain;
using Palladin.Module.Search.Contracts.Commands;
using Palladin.Module.Search.Contracts.ValueObjects;
using Palladin.Module.Search.Infrastructure.MassTransit;
using Palladin.Module.Search.Infrastructure.Persistence;
using JetBrains.Annotations;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Palladin.Module.Search.Features;

[UsedImplicitly]
internal sealed class RemoveSearchItemConsumerDefinition : ConsumerDefinition<RemoveSearchItemConsumer>
{
    public RemoveSearchItemConsumerDefinition() => EndpointName = SearchEndpoints.Items;
}

[UsedImplicitly]
internal sealed class RemoveSearchItemConsumer(
    SearchDomainWriteContext domainWriteContext) : IConsumer<RemoveSearchItemCommand>
{
    public async Task Consume(ConsumeContext<RemoveSearchItemCommand> context)
    {
        var msg = context.Message;
        var ct = context.CancellationToken;

        if (msg.Type is not (SearchItemTypes.Agent or SearchItemTypes.Member))
        {
            throw new InvalidOperationException(
                "Only server-visible administrative records can be removed from Search.");
        }

        var item = await domainWriteContext.Items.FirstOrDefaultAsync(
            i => i.OrganizationId == msg.OrganizationId && i.Id == msg.ItemId,
            ct);

        if (item is null)
        {
            domainWriteContext.Add(SearchItem.CreateRemovalTombstone(
                msg.ItemId,
                msg.OrganizationId,
                msg.Type,
                msg.OccurredAt));
            await domainWriteContext.CommitAsync(ct);
            return;
        }

        if (item.Type != msg.Type || msg.OccurredAt < item.UpdatedAt)
        {
            return;
        }

        item.Remove(msg.OccurredAt);
        await domainWriteContext.CommitAsync(ct);
    }
}
