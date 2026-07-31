using Palladin.Core.Api;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Palladin.Core.Persistence.Hooks;

[UsedImplicitly]
public class GenericDbMigrationApplicationStartingHook<TDbContext, TOptions>(
    TDbContext dbContext,
    IOptions<TOptions> options) : IApplicationStartingHook
    where TDbContext : DbContext
    where TOptions : class, IPersistenceOptions
{
    public async Task OnApplicationStartingAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.AutoMigration)
        {
            return;
        }

        var previousTimeout = dbContext.Database.GetCommandTimeout();
        dbContext.Database.SetCommandTimeout(TimeSpan.FromMinutes(100));

        try
        {
            await dbContext.Database.MigrateAsync(cancellationToken);
        }
        finally
        {
            dbContext.Database.SetCommandTimeout(previousTimeout);
        }
    }
}

public static class PersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddDbMigrationHook<TDbContext, TOptions>(
        this IServiceCollection services)
        where TDbContext : DbContext
        where TOptions : class, IPersistenceOptions
    {
        services.AddApplicationStartingHook<GenericDbMigrationApplicationStartingHook<TDbContext, TOptions>>();
        return services;
    }
}
