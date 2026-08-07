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
        services.Configure<GoogleOAuthOptions>(
            configuration.GetSection(GoogleOAuthOptions.Position));

        services.AddScoped<IExternalOAuthProvider, GoogleOAuthProvider>();

        return services;
    }
}
