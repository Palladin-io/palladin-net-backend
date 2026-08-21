using Palladin.Module.Identity.Infrastructure.Jwt;
using Palladin.Module.Identity.Infrastructure.Invitations;
using Palladin.Module.Identity.Infrastructure.Login;
using Palladin.Module.Identity.Infrastructure.PasswordAuth;
using Palladin.Module.Identity.Infrastructure.Totp;
using Palladin.Module.Identity.Infrastructure.Waitlist;
using Palladin.Module.Identity.Infrastructure.OAuth;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Palladin.Module.Identity.Infrastructure;

internal static class InfrastructureModule
{
    internal static IServiceCollection AddIdentityInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddIdentityPersistence(configuration);
        services.AddIdentityOAuth(configuration);
        services.AddIdentityJwt(configuration);
        services.AddIdentityWaitlist(configuration);
        services.AddIdentityPasswordAuth(configuration);
        services.AddIdentityTotp(configuration);
        services.AddIdentityLogin(configuration);
        services.AddOrganizationInvitations(configuration);
        return services;
    }
}
