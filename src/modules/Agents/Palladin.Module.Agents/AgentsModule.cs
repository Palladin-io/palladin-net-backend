using Palladin.Core.MassTransit;
using Palladin.Module.Agents.Infrastructure;
using JetBrains.Annotations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Palladin.Module.Agents;

[PublicAPI]
public static class AgentsModule
{
    public static IServiceCollection AddAgentsModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddAgentsInfrastructure(configuration);
        services.AddMassTransitAssembly(typeof(AgentsModule).Assembly);

        return services;
    }
}
