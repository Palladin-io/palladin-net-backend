using Palladin.Core.Security;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Shared;
using Palladin.Module.Identity.Domain;
using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record AssignableOrganizationPermissionItem(string Key, int Value, bool CanAssign);

[PublicAPI]
public sealed record ListOrganizationRolesResponse(
    IReadOnlyList<OrganizationRoleItem> Items,
    IReadOnlyList<AssignableOrganizationPermissionItem> AssignablePermissions);

[PublicAPI]
internal sealed class ListOrganizationRolesEndpoint(IdentityDomainReadContext domainReadContext)
    : EndpointWithoutRequest<ListOrganizationRolesResponse>
{
    public override void Configure()
    {
        Get("api/organization/roles");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.OrganizationManagement);
        this.RequireEmailVerified();
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
        var userId = User.GetUserId();
        if (organizationId is null || userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var actor = await domainReadContext.OrganizationMembers
            .Include(member => member.RoleAssignments)
                .ThenInclude(assignment => assignment.Role)
            .FirstOrDefaultAsync(member => member.OrganizationId == organizationId && member.UserId == userId, ct);
        if (actor is null)
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        var roleEntities = await domainReadContext.Roles
            .Where(role => role.OrganizationId == organizationId)
            .OrderByDescending(role => role.IsSystem)
            .ThenBy(role => role.Name)
            .Include(role => role.MemberAssignments)
            .ToListAsync(ct);
        var roles = roleEntities
            .Select(role => new OrganizationRoleItem(
                role.Id,
                role.Name,
                (int)role.Permissions,
                role.IsSystem,
                role.MemberAssignments.Count,
                OrganizationRoleAuthorization.CanAssignRole(
                    actor, role, allowAdministratorForOwner: true)))
            .ToArray();

        var assignablePermissions = OrganizationRolePermissions.Assignable
            .Select(permission => new AssignableOrganizationPermissionItem(
                permission.ToString(),
                (int)permission,
                actor.Status == OrganizationMemberStatus.Active
                && (actor.IsOwner || OrganizationRoleAuthorization.IsSubsetOf(
                    permission, actor.EffectivePermissions()))))
            .ToArray();

        await Send.OkAsync(new ListOrganizationRolesResponse(roles, assignablePermissions), ct);
    }
}
