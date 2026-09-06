using Palladin.Core.Persistence;
using Palladin.Module.Agents.Domain;
using Microsoft.EntityFrameworkCore;

namespace Palladin.Module.Agents.Infrastructure.Persistence;

internal sealed class AgentsDbReadContext(DbContextOptions<AgentsDbReadContext> options) : DbContext(options)
{
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<ApiKeyCredential> ApiKeyCredentials => Set<ApiKeyCredential>();
    public DbSet<AgentPairingRequest> AgentPairingRequests => Set<AgentPairingRequest>();
    public DbSet<AgentDisplayNameFence> AgentDisplayNameFences => Set<AgentDisplayNameFence>();
    public DbSet<User> Users => Set<User>();
    public DbSet<FormDiscoveryMap> FormDiscoveryMaps => Set<FormDiscoveryMap>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AgentsDbWriteContext).Assembly);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        throw new ReadOnlyContextSaveChangesException(nameof(AgentsDbReadContext));
}
