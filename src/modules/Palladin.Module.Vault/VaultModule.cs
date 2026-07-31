using Palladin.Core.MassTransit;
using Palladin.Module.Vault.Infrastructure;
using JetBrains.Annotations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Palladin.Module.Vault;

[PublicAPI]
public static class VaultModule
{
    public static IServiceCollection AddVaultModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddVaultInfrastructure(configuration);
        services.AddMassTransitAssembly(typeof(VaultModule).Assembly);

        return services;
    }
}
