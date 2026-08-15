using Microsoft.EntityFrameworkCore;
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
    public IQueryable<User> Users => Track<User>();
    public IQueryable<FormDiscoveryMap> FormDiscoveryMaps => Track<FormDiscoveryMap>();

    public async Task<int> LockAndLoadNextFormDiscoveryMapVersionAsync(
        Guid organizationId,
        string domain,
        string provider,
        CancellationToken cancellationToken)
    {
        await SqlQuery<int>(
            $"""
             SELECT 1 AS "Value"
             FROM pg_advisory_xact_lock(
                 hashtextextended({organizationId.ToString("N") + ":" + domain + ":" + provider}, 641084))
             """)
            .SingleAsync(cancellationToken);

        var latestVersion = await FormDiscoveryMaps
            .Where(x => x.Scope == FormDiscoveryMapScope.Organization
                && x.OrganizationId == organizationId
                && x.Domain == domain
                && x.Provider == provider)
            .Select(x => (int?)x.MapVersion)
            .MaxAsync(cancellationToken) ?? 0;
        return latestVersion + 1;
    }
}
