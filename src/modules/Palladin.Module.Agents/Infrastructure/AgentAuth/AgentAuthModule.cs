using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Palladin.Module.Agents.Infrastructure.AgentAuth;

[PublicAPI]
public static class AgentAuthModule
{
    internal static IServiceCollection AddAgentAuth(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<ApiKeyCacheOptions>(
            configuration.GetSection(ApiKeyCacheOptions.Position));

        services.Configure<AgentSignatureOptions>(
            configuration.GetSection(AgentSignatureOptions.Position));

        services.AddScoped<AgentSignatureVerifier>();

        return services;
    }

    public static AuthenticationBuilder AddAgentAuthentication(this AuthenticationBuilder builder) =>
        builder.AddScheme<AgentAuthenticationOptions, AgentAuthenticationHandler>(
            AgentAuthenticationOptions.SchemeName,
            _ => { });
}
