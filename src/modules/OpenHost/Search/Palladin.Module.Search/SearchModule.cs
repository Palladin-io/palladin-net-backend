using Palladin.Core.MassTransit;
using Palladin.Module.Search.Infrastructure;
using JetBrains.Annotations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Palladin.Module.Search;

// OpenHost read module. Owns a denormalized, polymorphic search read-model fed EXCLUSIVELY by its own
// integration commands (IndexSearchItemCommand / RemoveSearchItemCommand) —
// it never subscribes to another module's events and never issues a live cross-module query. The
// owning modules (Vault, Agents) translate their own domain events into these commands. Access is by
// scopes + permission, resolved entirely inside this module.
[PublicAPI]
public static class SearchModule
{
    public static IServiceCollection AddSearchModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSearchInfrastructure(configuration);
        services.AddMassTransitAssembly(typeof(SearchModule).Assembly);

        return services;
    }
}
