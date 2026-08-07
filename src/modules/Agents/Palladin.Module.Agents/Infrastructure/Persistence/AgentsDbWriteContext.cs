using Palladin.Module.Agents.Domain;
using Microsoft.EntityFrameworkCore;

namespace Palladin.Module.Agents.Infrastructure.Persistence;

internal sealed class AgentsDbWriteContext(DbContextOptions<AgentsDbWriteContext> options) : DbContext(options)
{
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AgentsDbWriteContext).Assembly);
    }
}
