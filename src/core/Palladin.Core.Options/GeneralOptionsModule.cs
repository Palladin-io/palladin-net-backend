using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Palladin.Core.Options;

public static class GeneralOptionsModule
{
    public static IServiceCollection AddGeneralOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<GeneralOptions>(configuration.GetSection(GeneralOptions.Position));
        services.Configure<NetworkingOptions>(configuration.GetSection(NetworkingOptions.Position));

        return services;
    }
}
