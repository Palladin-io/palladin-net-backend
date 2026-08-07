using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Palladin.Module.Identity.Infrastructure.Totp;

internal static class TotpModule
{
    private const string ConfigPrefix = "Modules:Identity";

    internal static IServiceCollection AddIdentityTotp(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<TotpOptions>(
            configuration.GetSection($"{ConfigPrefix}:{TotpOptions.Position}"));

        services.AddSingleton<ITotpService, TotpService>();

        return services;
    }
}
