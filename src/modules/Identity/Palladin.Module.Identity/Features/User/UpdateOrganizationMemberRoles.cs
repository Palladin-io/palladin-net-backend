using Palladin.Core.Api;
using Palladin.Core.Security;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Shared;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record UpdateOrganizationMemberRolesRequest
{
    public Guid UserId { get; init; }
    public IReadOnlyCollection<Guid> RoleIds { get; init; } = [];
}

[PublicAPI]
public sealed record UpdateOrganizationMemberRolesResponse(
    Guid UserId,
    IReadOnlyList<OrganizationRoleItem> Roles,
    int EffectivePermissions,
    uint AuthorizationVersion);

[UsedImplicitly]
internal sealed class UpdateOrganizationMemberRolesValidator : Validator<UpdateOrganizationMemberRolesRequest>
{
    public UpdateOrganizationMemberRolesValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.RoleIds)
            .Cascade(CascadeMode.Stop)
            .NotNull()
            .Must(roleIds => roleIds.Distinct().Count() == roleIds.Count);
    }
}

[PublicAPI]
internal sealed class UpdateOrganizationMemberRolesEndpoint(
    IdentityDomainWriteContext domainWriteContext,
    IClock clock) : Endpoint<UpdateOrganizationMemberRolesRequest, UpdateOrganizationMemberRolesResponse>
{
    public override void Configure()
    {
        Put("api/organization/members/{userId}/roles");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.OrganizationManagement);
        this.RequireEmailVerified();
        Tags("Identity/Organization");
        Summary(summary =>
        {
            summary.Summary = "Replace an organization member's roles";
            summary.Description = "Assigns a validated set of organization roles to a non-owner member.";
        });
    }

    public override async Task HandleAsync(UpdateOrganizationMemberRolesRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId();
        var changedBy = User.GetUserId();
        if (organizationId is null || changedBy is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var organization = await domainWriteContext.Organizations
            .SingleAsync(x => x.Id == organizationId, ct);
        organization.FenceMembershipMutation();

        var actor = await domainWriteContext.OrganizationMembers
            .Include(m => m.RoleAssignments)
                .ThenInclude(assignment => assignment.Role)
            .SingleAsync(m => m.OrganizationId == organizationId && m.UserId == changedBy, ct);
        if (actor.Status != OrganizationMemberStatus.Active)
        {
            AddError(ErrorResponses.General("organization-membership-inactive"));
            await Send.ErrorsAsync(403, ct);
            return;
        }

        var member = await domainWriteContext.OrganizationMembers
            .Include(m => m.RoleAssignments)
                .ThenInclude(assignment => assignment.Role)
            .Include(m => m.User)
            .FirstOrDefaultAsync(m => m.OrganizationId == organizationId && m.UserId == req.UserId, ct);
        if (member is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (member.IsOwner)
        {
            AddError(ErrorResponses.General("organization-owner-protected"));
            await Send.ErrorsAsync(409, ct);
            return;
        }

        if (member.Status != OrganizationMemberStatus.Active)
        {
            AddError(ErrorResponses.General("organization-member-removal-in-progress"));
            await Send.ErrorsAsync(409, ct);
            return;
        }

        var requestedRoleIds = req.RoleIds.ToHashSet();
        var roles = await domainWriteContext.Roles
            .Where(role => role.OrganizationId == organizationId && requestedRoleIds.Contains(role.Id))
            .ToListAsync(ct);
        if (roles.Count != requestedRoleIds.Count)
        {
            AddError(ErrorResponses.General("organization-role-invalid"));
            await Send.ErrorsAsync(400, ct);
            return;
        }

        var currentPermissions = member.EffectivePermissions();
        var proposedPermissions = roles.Aggregate(
            Permission.None,
            (permissions, role) => permissions | role.Permissions);
        if ((!actor.IsOwner
             && (!OrganizationRoleAuthorization.IsSubsetOf(currentPermissions, actor.EffectivePermissions())
                 || !OrganizationRoleAuthorization.IsSubsetOf(proposedPermissions, actor.EffectivePermissions())))
            || roles.Any(role => !OrganizationRoleAuthorization.CanAssignRole(
                actor, role, allowAdministratorForOwner: true)))
        {
            AddError(ErrorResponses.General("organization-role-assignment-forbidden"));
            await Send.ErrorsAsync(403, ct);
            return;
        }

        if (OrganizationRoleAuthorization.ChangesGrantManage(currentPermissions, proposedPermissions))
        {
            AddError(ErrorResponses.General("organization-role-grant-manage-cutover-unavailable"));
            await Send.ErrorsAsync(409, ct);
            return;
        }

        var now = clock.GetCurrentInstant();
        var changed = member.ReplaceRoles(
            roles, member.User.DisplayName, changedBy.Value, User.GetDisplayName(), now);
        if (changed)
        {
            await OrganizationRoleAuthorization.RevokeRefreshTokensAsync(
                domainWriteContext,
                organizationId.Value,
                [member.UserId],
                now,
                ct);
            await domainWriteContext.CommitAsync(ct);
        }

        await Send.OkAsync(ToResponse(member), ct);
    }

    private static UpdateOrganizationMemberRolesResponse ToResponse(OrganizationMember member) => new(
        member.UserId,
        member.RoleAssignments
            .Select(assignment => new OrganizationRoleItem(
                assignment.Role.Id,
                assignment.Role.Name,
                (int)assignment.Role.Permissions,
                assignment.Role.IsSystem,
                CanAssign: true))
            .OrderBy(role => role.Name)
            .ToArray(),
        (int)member.EffectivePermissions(),
        member.AuthorizationVersion);
}
