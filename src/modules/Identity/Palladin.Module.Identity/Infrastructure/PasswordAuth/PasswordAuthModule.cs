using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Palladin.Module.Identity.Infrastructure.PasswordAuth;

internal static class PasswordAuthModule
{
    private const string ConfigPrefix = "Modules:Identity";

    internal static IServiceCollection AddIdentityPasswordAuth(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<PasswordAuthOptions>()
            .Bind(configuration.GetSection($"{ConfigPrefix}:{PasswordAuthOptions.Position}"))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<PasswordAuthOptions>, PasswordAuthOptionsValidator>();

        services.AddSingleton<IPasswordHasher, Argon2idPasswordHasher>();
        services.AddScoped<ILoginSaltService, LoginSaltService>();

        return services;
    }
}
