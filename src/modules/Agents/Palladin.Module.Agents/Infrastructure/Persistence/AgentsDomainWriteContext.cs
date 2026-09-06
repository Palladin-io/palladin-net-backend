using Palladin.Core.Events;
using Palladin.Core.Persistence;
using Palladin.Module.Agents.Domain;

namespace Palladin.Module.Agents.Infrastructure.Persistence;

internal sealed class AgentsDomainWriteContext(
    AgentsDbWriteContext writeContext,
    IEnumerable<IEventPublisher> eventPublishers)
    : DomainWriteContextBase(writeContext, eventPublishers)
{
    public IQueryable<Agent> Agents => Track<Agent>();
    public IQueryable<ApiKey> ApiKeys => Track<ApiKey>();
    public IQueryable<ApiKeyCredential> ApiKeyCredentials => Track<ApiKeyCredential>();
    public IQueryable<AgentPairingRequest> AgentPairingRequests => Track<AgentPairingRequest>();
    public IQueryable<AgentDisplayNameFence> AgentDisplayNameFences => Track<AgentDisplayNameFence>();
    public IQueryable<User> Users => Track<User>();
    public IQueryable<FormDiscoveryMap> FormDiscoveryMaps => Track<FormDiscoveryMap>();
}
