using Palladin.Core.Persistence;
using Palladin.Module.Agents.Domain;

namespace Palladin.Module.Agents.Infrastructure.Persistence;

internal sealed class AgentsDomainReadContext(AgentsDbReadContext readContext) : DomainReadContextBase(readContext)
{
    public IQueryable<Agent> Agents => Query<Agent>();
    public IQueryable<ApiKey> ApiKeys => Query<ApiKey>();
    public IQueryable<User> Users => Query<User>();
    public IQueryable<FormDiscoveryMap> FormDiscoveryMaps => Query<FormDiscoveryMap>();
}
