using Palladin.Module.Identity.Infrastructure.Jwt;
using Palladin.Module.Identity.Infrastructure.Invitations;
using Palladin.Module.Identity.Infrastructure.Login;
using Palladin.Module.Identity.Infrastructure.PasswordAuth;
using Palladin.Module.Identity.Infrastructure.Totp;
using Palladin.Module.Identity.Infrastructure.Waitlist;
using Palladin.Module.Identity.Infrastructure.OAuth;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Features;
using Palladin.Core.Hangfire;
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
        services.AddOptions<DispatchVaultAccessReplicasJobOptions>()
            .Bind(configuration.GetSection(DispatchVaultAccessReplicasJobOptions.Position))
            .Validate(options => options.BatchSize is > 0 and <= 1000
                                 && options.RepairIntervalMinutes is >= 1 and <= 1440
                                 && !string.IsNullOrWhiteSpace(options.Expression),
                "Identity Vault-access dispatch settings are outside the supported bounds.")
            .ValidateOnStart();
        services.AddScopedCronJob<DispatchVaultAccessReplicasJob, DispatchVaultAccessReplicasJobOptions>(
            configuration.GetSection(DispatchVaultAccessReplicasJobOptions.Position));
        return services;
    }
}
