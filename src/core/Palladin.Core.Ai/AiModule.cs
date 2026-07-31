using Palladin.Core.Ai.EngineeringPlatform;
using Palladin.Core.Ai.SemanticKernel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Palladin.Core.Ai;

public static class AiModule
{
    public static IServiceCollection AddAiModule(
        this IServiceCollection serviceCollection,
        IConfiguration configuration)
    {
        return serviceCollection
            .AddEngineeringPlatformModule(configuration)
            .AddSemanticKernelModule(configuration);
    }
}
