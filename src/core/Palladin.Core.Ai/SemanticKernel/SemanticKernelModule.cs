using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;

namespace Palladin.Core.Ai.SemanticKernel;

internal static class SemanticKernelModule
{
    public static IServiceCollection AddSemanticKernelModule(
        this IServiceCollection serviceCollection,
        IConfiguration configuration)
    {
        var section = configuration.GetSection(SemanticKernelOptions.Position);
        var options = new SemanticKernelOptions();
        section.Bind(options);

        serviceCollection.Configure<SemanticKernelOptions>(section);
        var kernelBuilder = serviceCollection.AddKernel();

        foreach (var provider in options.GenericProviders)
        {
            kernelBuilder
                .AddOpenAIChatCompletion(
                    modelId: provider.Value.DefaultModelId,
                    serviceId: provider.Key,
                    apiKey: provider.Value.Key,
                    httpClient: new HttpClient { BaseAddress = new Uri(provider.Value.Url) }
                );
        }

        return serviceCollection
            .AddSingleton<IAiClient, SemanticKernelAiClient>();
    }
}
