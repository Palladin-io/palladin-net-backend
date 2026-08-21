using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Palladin.Core.Api;
using Palladin.Core.Persistence;
using Palladin.Core.Security;
using Palladin.Module.Identity.Infrastructure.Persistence;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record DeleteOrganizationRoleRequest
{
    public Guid RoleId { get; init; }
}

[UsedImplicitly]
internal sealed class DeleteOrganizationRoleValidator : Validator<DeleteOrganizationRoleRequest>
{
    public DeleteOrganizationRoleValidator() => RuleFor(x => x.RoleId).NotEmpty();
}

[PublicAPI]
internal sealed class DeleteOrganizationRoleEndpoint(
    IdentityDomainWriteContext domainWriteContext)
    : Endpoint<DeleteOrganizationRoleRequest>
{
    public override void Configure()
    {
        Delete("api/organization/roles/{roleId}");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.OrganizationManagement);
        this.RequireEmailVerified();
        Tags("Identity/Organization");
        Summary(summary =>
        {
            summary.Summary = "Delete an unused custom organization role";
            summary.Description = "Deletes only a custom role with no member assignments or invitation references.";
        });
    }

    public override async Task HandleAsync(DeleteOrganizationRoleRequest req, CancellationToken ct)
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

        if (!OrganizationRoleAuthorization.CanManageCustomRole(
                actor, role.Permissions, role.Permissions))
        {
            AddError(ErrorResponses.General("organization-role-assignment-forbidden"));
            await Send.ErrorsAsync(403, ct);
            return;
        }

        var isInUse = await domainWriteContext.OrganizationMemberRoles.AnyAsync(
                          assignment => assignment.OrganizationId == organizationId
                                        && assignment.RoleId == role.Id,
                          ct)
                      || await domainWriteContext.OrganizationInvitations.AnyAsync(
                          invitation => invitation.OrganizationId == organizationId
                                        && invitation.RoleId == role.Id,
                          ct);
        if (isInUse)
        {
            AddError(ErrorResponses.General("organization-role-in-use"));
            await Send.ErrorsAsync(409, ct);
            return;
        }

        domainWriteContext.Remove(role);

        try
        {
            await domainWriteContext.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
               { SqlState: Npgsql.PostgresErrorCodes.ForeignKeyViolation })
        {
            domainWriteContext.Clear();
            AddError(ErrorResponses.General("organization-role-in-use"));
            await Send.ErrorsAsync(409, ct);
            return;
        }

        await Send.NoContentAsync(ct);
    }
}
