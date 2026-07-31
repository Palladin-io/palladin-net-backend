using Palladin.Module.Search.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Palladin.Module.Search.Infrastructure;

internal static class InfrastructureModule
{
    internal static IServiceCollection AddSearchInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSearchPersistence(configuration);

        return services;
    }
}
