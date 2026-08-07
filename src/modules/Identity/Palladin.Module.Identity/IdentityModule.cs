using Palladin.Core.MassTransit;
using Palladin.Module.Identity.Infrastructure;
using JetBrains.Annotations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Palladin.Module.Identity;

[PublicAPI]
public static class IdentityModule
{
    public static IServiceCollection AddIdentityModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddIdentityInfrastructure(configuration);
        services.AddMassTransitAssembly(typeof(IdentityModule).Assembly);

        return services;
    }
}
