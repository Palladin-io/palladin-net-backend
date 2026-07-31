using Palladin.Core.Security;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Shared;
using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record OrganizationListItem(
    Guid Id,
    string Name,
    IReadOnlyList<OrganizationRoleItem> Roles,
    int EffectivePermissions,
    bool IsOwner,
    bool IsActive);

[PublicAPI]
public sealed record ListOrganizationsResponse(IReadOnlyList<OrganizationListItem> Items);

[PublicAPI]
internal sealed class ListOrganizationsEndpoint(IdentityDomainReadContext domainReadContext)
    : EndpointWithoutRequest<ListOrganizationsResponse>
{
    public override void Configure()
    {
        Get("api/organizations");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Tags("Identity/Organization");
        Summary(summary =>
        {
            summary.Summary = "List organizations available to the user";
            summary.Description = "Returns every organization membership and identifies the one selected by the current JWT.";
        });
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var userId = User.GetUserId();
        var activeOrganizationId = User.GetOrganizationId();
        if (userId is null || activeOrganizationId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var memberships = await domainReadContext.OrganizationMembers
            .Include(m => m.Organization)
            .Include(m => m.RoleAssignments)
                .ThenInclude(assignment => assignment.Role)
            .Where(m => m.UserId == userId)
            .OrderBy(m => m.Organization.Name)
            .ToListAsync(ct);

        var items = memberships.Select(m => new OrganizationListItem(
                m.OrganizationId,
                m.Organization.Name,
                m.RoleAssignments
                    .Select(assignment => new OrganizationRoleItem(
                        assignment.Role.Id,
                        assignment.Role.Name,
                        (int)assignment.Role.Permissions,
                        assignment.Role.IsSystem))
                    .OrderBy(role => role.Name)
                    .ToList(),
                (int)m.EffectivePermissions(),
                m.IsOwner,
                m.OrganizationId == activeOrganizationId))
            .ToList();

        await Send.OkAsync(new ListOrganizationsResponse(items), ct);
    }
}
