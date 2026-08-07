using Palladin.Module.Agents.Infrastructure.AgentAuth;
using Palladin.Module.Agents.Infrastructure.Persistence;
using Palladin.Module.Agents.Infrastructure.PublicAssets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Palladin.Module.Agents.Infrastructure;

internal static class InfrastructureModule
{
    internal static IServiceCollection AddAgentsInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddAgentsPersistence(configuration);
        services.AddAgentAuth(configuration);
        services.AddOptions<PublicAssetCatalogClientOptions>().Bind(configuration.GetSection(PublicAssetCatalogClientOptions.Position));
        services.AddHttpClient<IPublicAssetCatalogClient, PublicAssetCatalogClient>();

        return services;
    }
}
