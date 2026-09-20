using Palladin.Module.Identity.Infrastructure.Consents;
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
using Palladin.Core.Hangfire;
using Palladin.Module.Identity.Features;
using Palladin.Module.Identity.Infrastructure.SharedUnlock;
using Palladin.Module.Identity.Infrastructure.Sharing;

namespace Palladin.Module.Identity.Infrastructure;

internal static class InfrastructureModule
{
    internal static IServiceCollection AddIdentityInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton<ConsentNoticeCatalog>();
        services.AddOptions<ConsentOptions>()
            .Bind(configuration.GetSection(ConsentOptions.Position))
            .Validate(options => options.MaxAgeSeconds is > 0 and <= 300, "Consent freshness must be between 1 and 300 seconds.")
            .ValidateOnStart();
        services.AddIdentityPersistence(configuration);
        services.AddIdentityOAuth(configuration);
        services.AddIdentityJwt(configuration);
        services.AddIdentityWaitlist(configuration);
        services.AddIdentityPasswordAuth(configuration);
        services.AddIdentityTotp(configuration);
        services.AddIdentityLogin(configuration);
        services.AddOrganizationInvitations(configuration);
        services.AddOptions<EntrySharingRevocationOptions>()
            .Bind(configuration.GetSection(EntrySharingRevocationOptions.Position))
            .Validate(options => options.RequestTimeoutSeconds is >= 1 and <= 60,
                "Sharing revocation timeout must be between 1 and 60 seconds.")
            .ValidateOnStart();
        services.AddScoped<EntrySharingRevocation>();
        services.Configure<SharedUnlockOptions>(configuration.GetSection(SharedUnlockOptions.Position));
        services.AddScopedCronJob<CleanupSharedUnlockOperationsJob, CleanupSharedUnlockOperationsJobOptions>(
            configuration.GetSection(CleanupSharedUnlockOperationsJobOptions.Position));
        services.AddOptions<CleanupSharedUnlockOperationsJobOptions>()
            .Validate(options => !options.Enabled || (!string.IsNullOrWhiteSpace(options.Expression)
                && options.BatchSize is > 0 and <= 5000),
                "Shared-unlock cleanup requires a valid schedule and bounded batch size.")
            .ValidateOnStart();
        return services;
    }
}
