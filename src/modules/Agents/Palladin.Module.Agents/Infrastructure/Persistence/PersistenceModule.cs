using Palladin.Core.Persistence.Hooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Palladin.Module.Agents.Infrastructure.Persistence;

internal static class PersistenceModule
{
    internal static IServiceCollection AddAgentsPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<AgentsPersistenceOptions>(
            configuration.GetSection(AgentsPersistenceOptions.Position));

        services.AddDbContext<AgentsDbWriteContext>((sp, options) =>
        {
            var persistenceOptions = sp.GetRequiredService<IOptions<AgentsPersistenceOptions>>().Value;
            options.UseNpgsql(persistenceOptions.ConnectionString, npgsql => npgsql.UseNodaTime());
        });

        services.AddDbContext<AgentsDbReadContext>((sp, options) =>
        {
            var persistenceOptions = sp.GetRequiredService<IOptions<AgentsPersistenceOptions>>().Value;
            options.UseNpgsql(persistenceOptions.ConnectionString, npgsql => npgsql.UseNodaTime())
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
        });

        services.AddScoped<AgentsDomainReadContext>();
        services.AddScoped<AgentsDomainWriteContext>();
        services.AddScoped<AgentDisplayNameCoordinator>();
        services.AddDbMigrationHook<AgentsDbWriteContext, AgentsPersistenceOptions>();

        return services;
    }
}
