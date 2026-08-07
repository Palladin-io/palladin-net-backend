using Palladin.Core.Security;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Shared;
using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record ListOrganizationRolesResponse(IReadOnlyList<OrganizationRoleItem> Items);

[PublicAPI]
internal sealed class ListOrganizationRolesEndpoint(IdentityDomainReadContext domainReadContext)
    : EndpointWithoutRequest<ListOrganizationRolesResponse>
{
    public override void Configure()
    {
        Get("api/organization/roles");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Tags("Identity/Organization");
        Summary(summary =>
        {
            summary.Summary = "List organization roles";
            summary.Description = "Returns system and custom roles available in the active organization.";
        });
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId();
        if (organizationId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var roles = await domainReadContext.Roles
            .Where(role => role.OrganizationId == organizationId)
            .OrderByDescending(role => role.IsSystem)
            .ThenBy(role => role.Name)
            .Select(role => new OrganizationRoleItem(
                role.Id, role.Name, (int)role.Permissions, role.IsSystem))
            .ToListAsync(ct);

        await Send.OkAsync(new ListOrganizationRolesResponse(roles), ct);
    }
}
