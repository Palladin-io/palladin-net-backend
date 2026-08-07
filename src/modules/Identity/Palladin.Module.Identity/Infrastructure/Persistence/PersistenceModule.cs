using Palladin.Core.Persistence.Hooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Palladin.Module.Identity.Infrastructure.Persistence;

internal static class PersistenceModule
{
    internal static IServiceCollection AddIdentityPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<IdentityPersistenceOptions>(
            configuration.GetSection(IdentityPersistenceOptions.Position));

        services.AddDbContext<IdentityDbWriteContext>((sp, options) =>
        {
            var persistenceOptions = sp.GetRequiredService<IOptions<IdentityPersistenceOptions>>().Value;
            options.UseNpgsql(persistenceOptions.ConnectionString, npgsql => npgsql.UseNodaTime());
        });

        services.AddDbContext<IdentityDbReadContext>((sp, options) =>
        {
            var persistenceOptions = sp.GetRequiredService<IOptions<IdentityPersistenceOptions>>().Value;
            options.UseNpgsql(persistenceOptions.ConnectionString, npgsql => npgsql.UseNodaTime())
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
        });

        services.AddScoped<IdentityDomainReadContext>();
        services.AddScoped<IdentityDomainWriteContext>();
        services.AddDbMigrationHook<IdentityDbWriteContext, IdentityPersistenceOptions>();

        return services;
    }
}
