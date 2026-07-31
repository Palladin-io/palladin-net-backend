using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Palladin.Module.PublicAssetCatalog.Infrastructure.Persistence;
using Palladin.Module.PublicAssetCatalog.Infrastructure.Storage;
using Palladin.Module.PublicAssetCatalog.Infrastructure.Acquisition;

namespace Palladin.Module.PublicAssetCatalog.Infrastructure;

internal static class InfrastructureModule
{
    internal static IServiceCollection AddPublicAssetCatalogInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddPublicAssetCatalogPersistence(config);
        services.AddPublicAssetServiceAuthentication(config);
        services.Configure<PublicAssetStorageOptions>(config.GetSection(PublicAssetStorageOptions.Position));
        services.AddSingleton<IPublicAssetStorage, S3PublicAssetStorage>();
        services.AddHostedService<LocalPublicAssetBucketInitializer>();
        services.AddScoped<IWebsiteIconAcquirer, WebsiteIconAcquirer>();
        services.AddSingleton<WebsiteIconEnsureLimiter>();
        return services;
    }
}
