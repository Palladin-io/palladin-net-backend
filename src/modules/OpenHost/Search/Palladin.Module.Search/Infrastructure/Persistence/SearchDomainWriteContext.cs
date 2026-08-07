using Palladin.Core.Events;
using Palladin.Core.Persistence;
using Palladin.Module.Search.Domain;

namespace Palladin.Module.Search.Infrastructure.Persistence;

internal sealed class SearchDomainWriteContext(
    SearchDbWriteContext writeContext,
    IEnumerable<IEventPublisher> eventPublishers)
    : DomainWriteContextBase(writeContext, eventPublishers)
{
    public IQueryable<SearchItem> Items => Track<SearchItem>();
}
