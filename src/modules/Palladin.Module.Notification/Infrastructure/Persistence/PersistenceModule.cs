using Palladin.Core.Persistence.Hooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Palladin.Module.Notification.Infrastructure.Persistence;

internal static class PersistenceModule
{
    internal static IServiceCollection AddNotificationPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<NotificationPersistenceOptions>(
            configuration.GetSection(NotificationPersistenceOptions.Position));

        services.AddDbContext<NotificationDbWriteContext>((sp, options) =>
        {
            var persistenceOptions = sp.GetRequiredService<IOptions<NotificationPersistenceOptions>>().Value;
            options.UseNpgsql(persistenceOptions.ConnectionString, npgsql => npgsql.UseNodaTime());
        });

        services.AddDbContext<NotificationDbReadContext>((sp, options) =>
        {
            var persistenceOptions = sp.GetRequiredService<IOptions<NotificationPersistenceOptions>>().Value;
            options.UseNpgsql(persistenceOptions.ConnectionString, npgsql => npgsql.UseNodaTime())
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
        });

        services.AddScoped<NotificationDomainReadContext>();
        services.AddScoped<NotificationDomainWriteContext>();
        services.AddDbMigrationHook<NotificationDbWriteContext, NotificationPersistenceOptions>();

        return services;
    }
}
