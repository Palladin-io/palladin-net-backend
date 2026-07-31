using Microsoft.Extensions.DependencyInjection;

namespace Palladin.Core.AbTesting;

public static class AbTestingModule
{
    public static IServiceCollection AddAbTestingModule(this IServiceCollection services)
    {
        return services.AddSingleton<IActionSelector, RoundRobinSelector>();
    }
}
