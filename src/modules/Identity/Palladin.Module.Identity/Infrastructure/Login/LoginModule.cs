using Palladin.Core.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Palladin.Module.Identity.Infrastructure.Login;

internal static class LoginModule
{
    private const string ConfigPrefix = "Modules:Identity";

    internal static IServiceCollection AddIdentityLogin(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<LoginThrottleOptions>(
            configuration.GetSection($"{ConfigPrefix}:{LoginThrottleOptions.Position}"));
        services.Configure<EmailVerificationOptions>(
            configuration.GetSection($"{ConfigPrefix}:{EmailVerificationOptions.Position}"));

        services.AddScoped<ILoginThrottleService, LoginThrottleService>();
        services.AddSingleton<IEmailVerificationGate, EmailVerificationGate>();

        return services;
    }
}
