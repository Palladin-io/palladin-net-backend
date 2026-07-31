using Microsoft.Extensions.DependencyInjection;

namespace Palladin.Core.Guid;

public static class GuidModule
{
    public static IServiceCollection AddGuidModule(this IServiceCollection services)
    {
        services.AddSingleton<IGuidProvider, GuidProvider>();

        return services;
    }
}
