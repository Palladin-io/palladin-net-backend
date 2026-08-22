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
internal sealed class IndexSearchItemConsumerDefinition : ConsumerDefinition<IndexSearchItemConsumer>
{
    public IndexSearchItemConsumerDefinition() => EndpointName = SearchEndpoints.Items;
}

[UsedImplicitly]
internal sealed class IndexSearchItemConsumer(
    SearchDomainWriteContext domainWriteContext) : IConsumer<IndexSearchItemCommand>
{
    private const int MaxNameLength = 256;
    private const int MaxSearchTextLength = 1024;

    public async Task Consume(ConsumeContext<IndexSearchItemCommand> context)
    {
        var msg = context.Message;
        var ct = context.CancellationToken;

        if (msg.Type is not (SearchItemTypes.Agent or SearchItemTypes.Member))
        {
            throw new InvalidOperationException(
                "Only server-visible administrative records can be indexed by Search.");
        }

        var name = Truncate(msg.Name.Trim(), MaxNameLength);
        var searchText = BuildSearchText(name, msg.SearchTerms);

        var existing = await domainWriteContext.Items.FirstOrDefaultAsync(
            i => i.OrganizationId == msg.OrganizationId && i.Id == msg.ItemId,
            ct);

        if (existing is null)
        {
            domainWriteContext.Add(SearchItem.Create(
                msg.ItemId,
                msg.OrganizationId,
                msg.Type,
                name,
                searchText,
                msg.UpdatedAt));
            await domainWriteContext.CommitAsync(ct);
            return;
        }

        if (msg.UpdatedAt <= existing.UpdatedAt)
        {
            return;
        }

        if (existing.Type != msg.Type)
        {
            throw new InvalidOperationException("Search item type cannot change for an existing identifier.");
        }

        existing.Apply(
            msg.OrganizationId,
            name,
            searchText,
            msg.UpdatedAt);
        await domainWriteContext.CommitAsync(ct);
    }

    private static string BuildSearchText(string name, IReadOnlyList<string> searchTerms)
    {
        var terms = searchTerms
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .Select(term => term.Trim())
            .Take(4);
        return Truncate(string.Join(' ', new[] { name }.Concat(terms)), MaxSearchTextLength);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
