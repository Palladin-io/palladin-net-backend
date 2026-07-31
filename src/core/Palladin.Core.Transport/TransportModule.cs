using Microsoft.Extensions.DependencyInjection;

namespace Palladin.Core.Transport;

public static class TransportModule
{
    public static IServiceCollection AddTransportModule(this IServiceCollection services)
    {
        services.AddScoped<ITransportContext, TransportContext>();

        return services;
    }
}
