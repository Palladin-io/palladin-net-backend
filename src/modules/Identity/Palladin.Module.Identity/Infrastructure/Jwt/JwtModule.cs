using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Palladin.Core.Security;
using Palladin.Module.Identity.Infrastructure.Options;
using Palladin.Module.Identity.Shared;

namespace Palladin.Module.Identity.Infrastructure.Jwt;

internal static class JwtModule
{
    internal static IServiceCollection AddIdentityJwt(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.Position))
            .Validate(
                options => Encoding.UTF8.GetByteCount(options.Secret) >= 32,
                "Identity JWT signing secret must contain at least 32 bytes.")
            .ValidateOnStart();

        services.AddSingleton<IServerKeyDeriver, JwtServerKeyDeriver>();
        services.AddScoped<ITokenService, TokenService>();
        services.AddScoped<IAuthSessionIssuer, AuthSessionIssuer>();
        services.AddScoped<IOrganizationMembershipValidator, OrganizationMembershipValidator>();

        return services;
    }
}
