using Palladin.Module.Identity.Infrastructure.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Palladin.Module.Identity.Infrastructure.OAuth;

internal static class OAuthModule
{
    internal static IServiceCollection AddIdentityOAuth(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<GoogleOAuthOptions>()
            .Bind(configuration.GetSection(GoogleOAuthOptions.Position))
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.ClientId),
                "Google OAuth client ID must be configured.")
            .ValidateOnStart();

        services.AddScoped<IExternalOAuthProvider, GoogleOAuthProvider>();

        return services;
    }
}
