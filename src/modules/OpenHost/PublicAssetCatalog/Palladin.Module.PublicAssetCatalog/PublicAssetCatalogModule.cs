using JetBrains.Annotations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Palladin.Core.MassTransit;
using Palladin.Module.PublicAssetCatalog.Infrastructure;

namespace Palladin.Module.PublicAssetCatalog;

[PublicAPI]
public static class PublicAssetCatalogModule
{
    public static IServiceCollection AddPublicAssetCatalogModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddPublicAssetCatalogInfrastructure(configuration);
        services.AddMassTransitAssembly(typeof(PublicAssetCatalogModule).Assembly);
        return services;
    }
}
