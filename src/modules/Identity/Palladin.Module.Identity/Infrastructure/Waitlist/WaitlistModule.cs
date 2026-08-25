using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Palladin.Module.Identity.Infrastructure.Waitlist;

internal static class WaitlistModule
{
    // Leaf Position plus module prefix are composed at registration.
    private const string ConfigPrefix = "Modules:Identity";

    internal static IServiceCollection AddIdentityWaitlist(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<WaitlistOptions>()
            .Bind(configuration.GetSection($"{ConfigPrefix}:{WaitlistOptions.Position}"))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<WaitlistOptions>, WaitlistOptionsValidator>();
        services.AddSingleton<WaitlistBenefitPolicy>();

        return services;
    }
}
