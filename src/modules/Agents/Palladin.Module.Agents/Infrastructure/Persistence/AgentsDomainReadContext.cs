using Palladin.Core.Persistence;
using Palladin.Module.Agents.Domain;

namespace Palladin.Module.Agents.Infrastructure.Persistence;

internal sealed class AgentsDomainReadContext(AgentsDbReadContext readContext) : DomainReadContextBase(readContext)
{
    public IQueryable<Agent> Agents => Query<Agent>();
    public IQueryable<ApiKey> ApiKeys => Query<ApiKey>();
    public IQueryable<ApiKeyCredential> ApiKeyCredentials => Query<ApiKeyCredential>();
    public IQueryable<AgentPairingRequest> AgentPairingRequests => Query<AgentPairingRequest>();
    public IQueryable<AgentDisplayNameFence> AgentDisplayNameFences => Query<AgentDisplayNameFence>();
    public IQueryable<User> Users => Query<User>();
    public IQueryable<FormDiscoveryMap> FormDiscoveryMaps => Query<FormDiscoveryMap>();
}
