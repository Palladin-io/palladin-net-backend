using Palladin.Core.Persistence.Hooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Palladin.Module.Search.Infrastructure.Persistence;

internal static class PersistenceModule
{
    internal static IServiceCollection AddSearchPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<SearchPersistenceOptions>(
            configuration.GetSection(SearchPersistenceOptions.Position));

        services.AddDbContext<SearchDbWriteContext>((sp, options) =>
        {
            var persistenceOptions = sp.GetRequiredService<IOptions<SearchPersistenceOptions>>().Value;
            options.UseNpgsql(persistenceOptions.ConnectionString, npgsql => npgsql.UseNodaTime());
        });

        services.AddDbContext<SearchDbReadContext>((sp, options) =>
        {
            var persistenceOptions = sp.GetRequiredService<IOptions<SearchPersistenceOptions>>().Value;
            options.UseNpgsql(persistenceOptions.ConnectionString, npgsql => npgsql.UseNodaTime())
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
        });

        services.AddScoped<SearchDomainReadContext>();
        services.AddScoped<SearchDomainWriteContext>();
        services.AddDbMigrationHook<SearchDbWriteContext, SearchPersistenceOptions>();

        return services;
    }
}
