using Palladin.Core.Ai.EngineeringPlatform.Langfuse;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Palladin.Core.Ai.EngineeringPlatform;

internal static class EngineeringPlatformModule
{
    public static IServiceCollection AddEngineeringPlatformModule(this IServiceCollection services,
        IConfiguration configuration)
    {
        var optionsSection = configuration.GetSection(EngineeringPlatformOptions.Position);
        var options = new EngineeringPlatformOptions();
        optionsSection.Bind(options);

        services.Configure<EngineeringPlatformOptions>(optionsSection);

        return options.Provider switch
        {
            EngineeringPlatformProvider.Langfuse => services.AddLangfuseModule(optionsSection),
            _ => throw new ArgumentOutOfRangeException()
        };
    }
}
