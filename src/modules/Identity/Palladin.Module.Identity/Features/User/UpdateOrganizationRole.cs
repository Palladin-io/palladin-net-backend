using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;
using Palladin.Core.Api;
using Palladin.Core.Persistence;
using Palladin.Core.Security;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Shared;
using Palladin.Module.Identity.Infrastructure.Sharing;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record UpdateOrganizationRoleRequest
{
    public Guid RoleId { get; init; }
    public string Name { get; init; } = string.Empty;
    public int Permissions { get; init; }
}

[UsedImplicitly]
internal sealed class UpdateOrganizationRoleValidator : Validator<UpdateOrganizationRoleRequest>
{
    public UpdateOrganizationRoleValidator()
    {
        RuleFor(x => x.RoleId).NotEmpty();
        RuleFor(x => x.Name)
            .Must(name => !string.IsNullOrWhiteSpace(name))
            .MaximumLength(100);
        RuleFor(x => x.Permissions)
            .Must(value => OrganizationRolePermissions.IsAssignable((Permission)value));
    }
}

[PublicAPI]
internal sealed class UpdateOrganizationRoleEndpoint(
    IdentityDomainWriteContext domainWriteContext,
    EntrySharingRevocation sharingRevocation,
    IClock clock) : Endpoint<UpdateOrganizationRoleRequest, OrganizationRoleItem>
{
    public override void Configure()
    {
        Put("api/organization/roles/{roleId}");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.OrganizationManagement);
        this.RequireEmailVerified();
        Tags("Identity/Organization");
        Summary(summary =>
        {
            summary.Summary = "Update a custom organization role";
            summary.Description = "Updates a custom role and immediately invalidates affected authorization sessions. GrantManage eligibility changes fail closed until the Vault recipient cutover exists.";
        });
    }

    public override async Task HandleAsync(UpdateOrganizationRoleRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId();
        var userId = User.GetUserId();
        if (organizationId is null || userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var organization = await domainWriteContext.Organizations.SingleAsync(
            x => x.Id == organizationId, ct);
        organization.FenceMembershipMutation();

        var actor = await domainWriteContext.OrganizationMembers
            .Include(member => member.RoleAssignments)
                .ThenInclude(assignment => assignment.Role)
            .SingleAsync(member => member.OrganizationId == organizationId && member.UserId == userId, ct);
        var role = await domainWriteContext.Roles.FirstOrDefaultAsync(
            x => x.OrganizationId == organizationId && x.Id == req.RoleId, ct);
        if (role is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (role.IsSystem)
        {
            AddError(ErrorResponses.General("organization-system-role-immutable"));
            await Send.ErrorsAsync(409, ct);
            return;
        }

        var requestedPermissions = (Permission)req.Permissions;
        if (!OrganizationRoleAuthorization.CanManageCustomRole(
                actor, role.Permissions, requestedPermissions))
        {
            AddError(ErrorResponses.General("organization-role-assignment-forbidden"));
            await Send.ErrorsAsync(403, ct);
            return;
        }

        var normalizedName = Role.NormalizeName(req.Name);
        if (await domainWriteContext.Roles.AnyAsync(
                other => other.OrganizationId == organizationId
                         && other.Id != role.Id
                         && other.NormalizedName == normalizedName,
                ct))
        {
            AddError(ErrorResponses.General("organization-role-name-conflict"));
            await Send.ErrorsAsync(409, ct);
            return;
        }

        var assignedMembers = await domainWriteContext.OrganizationMembers
            .Include(member => member.RoleAssignments)
                .ThenInclude(assignment => assignment.Role)
            .Where(member => member.OrganizationId == organizationId
                             && member.RoleAssignments.Any(assignment => assignment.RoleId == role.Id))
            .ToListAsync(ct);

        var authorizationChanges = assignedMembers
            .Select(member => new
            {
                Member = member,
                Current = member.EffectivePermissions(),
                Proposed = member.RoleAssignments.Aggregate(
                    Permission.None,
                    (permissions, assignment) => permissions
                        | (assignment.RoleId == role.Id ? requestedPermissions : assignment.Role.Permissions)),
            })
            .Where(change => change.Current != change.Proposed)
            .ToArray();
        if (!actor.IsOwner && authorizationChanges.Any(change =>
                !OrganizationRoleAuthorization.IsSubsetOf(
                    change.Current, actor.EffectivePermissions())
                || !OrganizationRoleAuthorization.IsSubsetOf(
                    change.Proposed, actor.EffectivePermissions())))
        {
            AddError(ErrorResponses.General("organization-role-assignment-forbidden"));
            await Send.ErrorsAsync(403, ct);
            return;
        }

        if (authorizationChanges.Any(change =>
                OrganizationRoleAuthorization.ChangesGrantManage(change.Current, change.Proposed)))
        {
            AddError(ErrorResponses.General("organization-role-grant-manage-cutover-unavailable"));
            await Send.ErrorsAsync(409, ct);
            return;
        }

        if (role.Name == req.Name.Trim() && role.Permissions == requestedPermissions)
        {
            await Send.OkAsync(new OrganizationRoleItem(
                role.Id, role.Name, (int)role.Permissions, role.IsSystem,
                assignedMembers.Count, CanAssign: true), ct);
            return;
        }

        var now = clock.GetCurrentInstant();
        foreach (var change in authorizationChanges.Where(change =>
                     change.Current.HasFlag(Permission.VaultManage)
                     && !change.Proposed.HasFlag(Permission.VaultManage)))
        {
            await sharingRevocation.RevokeMemberAsync(
                organizationId.Value, change.Member.UserId, change.Member.AuthorizationVersion, now, ct);
        }

        role.UpdateCustom(req.Name, requestedPermissions, now);
        foreach (var change in authorizationChanges)
        {
            change.Member.InvalidateAuthorization(now);
        }

        await OrganizationRoleAuthorization.RevokeRefreshTokensAsync(
            domainWriteContext,
            organizationId.Value,
            authorizationChanges.Select(change => change.Member.UserId).ToArray(),
            now,
            ct);
        try
        {
            await domainWriteContext.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
               { SqlState: Palladin.Core.Persistence.PostgresErrorCodes.UniqueViolation })
        {
            domainWriteContext.Clear();
            AddError(ErrorResponses.General("organization-role-name-conflict"));
            await Send.ErrorsAsync(409, ct);
            return;
        }

        await Send.OkAsync(new OrganizationRoleItem(
            role.Id, role.Name, (int)role.Permissions, role.IsSystem,
            assignedMembers.Count, CanAssign: true), ct);
    }
}
