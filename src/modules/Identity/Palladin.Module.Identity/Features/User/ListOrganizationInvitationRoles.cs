using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Palladin.Core.Security;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure.Persistence;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record OrganizationInvitationRoleItem(Guid Id, string Name);

[PublicAPI]
public sealed record ListOrganizationInvitationRolesResponse(
    IReadOnlyList<OrganizationInvitationRoleItem> Items);

[PublicAPI]
internal sealed class ListOrganizationInvitationRolesEndpoint(
    IdentityDomainReadContext domainReadContext)
    : EndpointWithoutRequest<ListOrganizationInvitationRolesResponse>
{
    public override void Configure()
    {
        Get("api/organization/invitation-roles");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.AddUser);
        this.RequireEmailVerified();
        Tags("Identity/Organization");
        Summary(summary =>
        {
            summary.Summary = "List roles assignable to an organization invitation";
            summary.Description = "Returns the minimal role selector available to members who can invite users.";
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
        if (actor is null || actor.Status != OrganizationMemberStatus.Active)
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        var roleEntities = await domainReadContext.Roles
            .Where(role => role.OrganizationId == organizationId)
            .OrderByDescending(role => role.IsSystem)
            .ThenBy(role => role.Name)
            .ToListAsync(ct);
        var roles = roleEntities
            .Where(role => OrganizationRoleAuthorization.CanAssignRoleDuringGrantManageCutover(
                actor, role, allowAdministratorForOwner: false))
            .Select(role => new OrganizationInvitationRoleItem(role.Id, role.Name))
            .ToArray();

        await Send.OkAsync(new ListOrganizationInvitationRolesResponse(roles), ct);
    }
}
