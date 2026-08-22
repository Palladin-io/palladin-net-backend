using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Palladin.Core.Security;

namespace Palladin.Module.Identity.Infrastructure.Login;

internal static class LoginModule
{
    private const string ConfigPrefix = "Modules:Identity";

    internal static IServiceCollection AddIdentityLogin(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<LoginThrottleOptions>()
            .Bind(configuration.GetSection($"{ConfigPrefix}:{LoginThrottleOptions.Position}"))
            .Validate(ValidateLoginThrottle, "Login throttle limits, windows, and retry count must be positive.")
            .ValidateOnStart();
        services.Configure<EmailVerificationOptions>(
            configuration.GetSection($"{ConfigPrefix}:{EmailVerificationOptions.Position}"));

        services.AddScoped<ILoginThrottleService, LoginThrottleService>();
        services.AddScoped<ILoginRateLimiter, LoginRateLimiter>();
        services.AddSingleton<IEmailVerificationGate, EmailVerificationGate>();

        return services;
    }

    private static bool ValidateLoginThrottle(LoginThrottleOptions options) =>
        options.MaxAttempts > 0
        && options.WindowMinutes > 0
        && options.LockoutMinutes > 0
        && options.RateLimitWindowSeconds > 0
        && options.LoginIpPermitLimit > 0
        && options.LoginAccountPermitLimit > 0
        && options.TotpIpPermitLimit > 0
        && options.TotpAccountPermitLimit > 0
        && options.ConcurrencyRetryLimit > 0;
}
