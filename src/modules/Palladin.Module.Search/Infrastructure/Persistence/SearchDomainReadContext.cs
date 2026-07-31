using Palladin.Core.Persistence;
using Palladin.Module.Search.Domain;

namespace Palladin.Module.Search.Infrastructure.Persistence;

internal sealed class SearchDomainReadContext(SearchDbReadContext readContext) : DomainReadContextBase(readContext)
{
    public IQueryable<SearchItem> Items => Query<SearchItem>();
}
