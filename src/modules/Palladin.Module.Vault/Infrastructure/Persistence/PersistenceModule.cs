using Palladin.Core.Persistence.Hooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Palladin.Module.Vault.Infrastructure.Persistence;

internal static class PersistenceModule
{
    internal static IServiceCollection AddVaultPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<VaultPersistenceOptions>(
            configuration.GetSection(VaultPersistenceOptions.Position));

        services.AddDbContext<VaultDbWriteContext>((sp, options) =>
        {
            var persistenceOptions = sp.GetRequiredService<IOptions<VaultPersistenceOptions>>().Value;
            options.UseNpgsql(persistenceOptions.ConnectionString, npgsql => npgsql.UseNodaTime());
        });

        services.AddDbContext<VaultDbReadContext>((sp, options) =>
        {
            var persistenceOptions = sp.GetRequiredService<IOptions<VaultPersistenceOptions>>().Value;
            options.UseNpgsql(persistenceOptions.ConnectionString, npgsql => npgsql.UseNodaTime())
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
        });

        services.AddScoped<VaultDomainReadContext>();
        services.AddScoped<VaultDomainWriteContext>();
        services.AddDbMigrationHook<VaultDbWriteContext, VaultPersistenceOptions>();

        return services;
    }
}
