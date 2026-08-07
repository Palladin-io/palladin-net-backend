using Palladin.Core.Persistence.Hooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Palladin.Module.Audit.Infrastructure.Persistence;

internal static class PersistenceModule
{
    internal static IServiceCollection AddAuditPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<AuditPersistenceOptions>(
            configuration.GetSection(AuditPersistenceOptions.Position));

        services.AddDbContext<AuditDbWriteContext>((sp, options) =>
        {
            var persistenceOptions = sp.GetRequiredService<IOptions<AuditPersistenceOptions>>().Value;
            options.UseNpgsql(persistenceOptions.ConnectionString, npgsql => npgsql.UseNodaTime());
        });

        services.AddDbContext<AuditDbReadContext>((sp, options) =>
        {
            var persistenceOptions = sp.GetRequiredService<IOptions<AuditPersistenceOptions>>().Value;
            options.UseNpgsql(persistenceOptions.ConnectionString, npgsql => npgsql.UseNodaTime())
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
        });

        services.AddScoped<AuditDomainReadContext>();
        services.AddScoped<AuditDomainWriteContext>();
        services.AddDbMigrationHook<AuditDbWriteContext, AuditPersistenceOptions>();

        return services;
    }
}
