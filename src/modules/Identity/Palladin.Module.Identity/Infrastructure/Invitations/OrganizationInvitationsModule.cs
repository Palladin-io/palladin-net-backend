using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Palladin.Module.Identity.Infrastructure.Invitations;

internal static class OrganizationInvitationsModule
{
    internal static IServiceCollection AddOrganizationInvitations(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<OrganizationInvitationOptions>()
            .Bind(configuration.GetSection(OrganizationInvitationOptions.Position))
            .Validate(options => options.TokenTtlHours > 0,
                "Organization invitation token TTL must be positive.")
            .Validate(options => options.ResendCooldownSeconds >= 0,
                "Organization invitation resend cooldown cannot be negative.")
            .ValidateOnStart();

        return services;
    }
}
