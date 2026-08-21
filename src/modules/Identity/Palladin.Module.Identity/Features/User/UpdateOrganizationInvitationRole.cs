using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Api;
using Palladin.Core.Security;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure.Persistence;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record UpdateOrganizationInvitationRoleRequest
{
    public Guid InvitationId { get; init; }
    public Guid RoleId { get; init; }
}

[UsedImplicitly]
internal sealed class UpdateOrganizationInvitationRoleValidator
    : Validator<UpdateOrganizationInvitationRoleRequest>
{
    public UpdateOrganizationInvitationRoleValidator()
    {
        RuleFor(request => request.InvitationId).NotEmpty();
        RuleFor(request => request.RoleId).NotEmpty();
    }
}

[PublicAPI]
internal sealed class UpdateOrganizationInvitationRoleEndpoint(
    IdentityDomainWriteContext domainWriteContext,
    IClock clock)
    : Endpoint<UpdateOrganizationInvitationRoleRequest>
{
    public override void Configure()
    {
        Put("api/organization/invitations/{invitationId}/role");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.AddUser);
        this.RequireEmailVerified();
        Tags("Identity/Organization");
        Summary(summary =>
        {
            summary.Summary = "Change a pending organization invitation role";
            summary.Description = "Replaces the invitation-safe role that will be assigned when the recipient accepts the existing link.";
        });
    }

    public override async Task HandleAsync(
        UpdateOrganizationInvitationRoleRequest req,
        CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId();
        var userId = User.GetUserId();
        if (organizationId is null || userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var organization = await domainWriteContext.Organizations
            .SingleAsync(candidate => candidate.Id == organizationId, ct);
        organization.FenceMembershipMutation();

        var actor = await domainWriteContext.OrganizationMembers
            .Include(member => member.User)
            .Include(member => member.RoleAssignments)
                .ThenInclude(assignment => assignment.Role)
            .SingleAsync(member => member.OrganizationId == organizationId
                                   && member.UserId == userId, ct);
        var invitation = await domainWriteContext.OrganizationInvitations
            .FirstOrDefaultAsync(candidate => candidate.OrganizationId == organizationId
                                              && candidate.Id == req.InvitationId, ct);
        if (invitation is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var now = clock.GetCurrentInstant();
        if (!invitation.IsPending(now))
        {
            AddError(ErrorResponses.General("organization-invitation-not-pending"));
            await Send.ErrorsAsync(409, ct);
            return;
        }

        var role = await domainWriteContext.Roles.FirstOrDefaultAsync(
            candidate => candidate.OrganizationId == organizationId && candidate.Id == req.RoleId,
            ct);
        if (role is null)
        {
            AddError(ErrorResponses.General("organization-role-invalid"));
            await Send.ErrorsAsync(400, ct);
            return;
        }

        if (role.IsSystem && !role.IsDefaultUser)
        {
            AddError(ErrorResponses.General("organization-role-assignment-forbidden"));
            await Send.ErrorsAsync(403, ct);
            return;
        }

        if (OrganizationRolePermissions.HasGrantManage(role.Permissions))
        {
            AddError(ErrorResponses.General("organization-role-grant-manage-cutover-unavailable"));
            await Send.ErrorsAsync(409, ct);
            return;
        }

        if (!OrganizationRoleAuthorization.CanAssignRole(
                actor,
                role,
                allowAdministratorForOwner: false))
        {
            AddError(ErrorResponses.General("organization-role-assignment-forbidden"));
            await Send.ErrorsAsync(403, ct);
            return;
        }

        if (!invitation.ChangeRole(role.Id, role.Name, userId.Value, actor.User.DisplayName, now))
        {
            await Send.NoContentAsync(ct);
            return;
        }

        await domainWriteContext.CommitAsync(ct);
        await Send.NoContentAsync(ct);
    }
}
