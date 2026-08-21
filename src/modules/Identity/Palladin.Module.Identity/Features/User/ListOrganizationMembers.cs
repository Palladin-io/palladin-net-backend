using Palladin.Core.Security;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Shared;
using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record OrganizationMemberItem(
    Guid UserId,
    string DisplayName,
    string Email,
    string? PublicKey,
    IReadOnlyList<OrganizationRoleItem> Roles,
    int EffectivePermissions,
    bool IsOwner,
    Instant JoinedAt,
    string Status,
    uint AuthorizationVersion);

[PublicAPI]
public sealed record ListOrganizationMembersResponse(IReadOnlyList<OrganizationMemberItem> Items);

[PublicAPI]
internal sealed class ListOrganizationMembersEndpoint(IdentityDomainReadContext domainReadContext)
    : EndpointWithoutRequest<ListOrganizationMembersResponse>
{
    public override void Configure()
    {
        Get("api/organization/members");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Tags("Identity/Organization");
        Summary(summary =>
        {
            summary.Summary = "List organization members";
            summary.Description = "Returns members of the active organization with their role and join date.";
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

        var members = await domainReadContext.OrganizationMembers
            .Include(m => m.User)
            .Include(m => m.RoleAssignments)
                .ThenInclude(assignment => assignment.Role)
            .Where(m => m.OrganizationId == organizationId)
            .OrderByDescending(m => m.IsOwner)
            .ThenBy(m => m.User.DisplayName)
            .ToListAsync(ct);

        var items = members.Select(m => new OrganizationMemberItem(
                m.UserId,
                m.User.DisplayName,
                m.User.Email,
                m.User.PublicKey != null ? Convert.ToBase64String(m.User.PublicKey) : null,
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
                m.JoinedAt,
                m.Status.ToString(),
                m.AuthorizationVersion))
            .ToList();

        await Send.OkAsync(new ListOrganizationMembersResponse(items), ct);
    }
}
