using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Palladin.Module.Identity.Infrastructure.Invitations;

internal static class OrganizationInvitationsModule
{
    internal static IServiceCollection AddOrganizationInvitations(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<OrganizationInvitationOptions>(
            configuration.GetSection(OrganizationInvitationOptions.Position));

        return services;
    }
}
