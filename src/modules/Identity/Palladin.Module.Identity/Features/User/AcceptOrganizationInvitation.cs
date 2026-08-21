using Palladin.Core.Api;
using Palladin.Core.Persistence;
using Palladin.Core.Security;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure;
using Palladin.Module.Identity.Infrastructure.Persistence;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record AcceptOrganizationInvitationRequest
{
    public string Token { get; init; } = string.Empty;
}

[UsedImplicitly]
internal sealed class AcceptOrganizationInvitationValidator : Validator<AcceptOrganizationInvitationRequest>
{
    public AcceptOrganizationInvitationValidator() => RuleFor(x => x.Token).NotEmpty();
}

[PublicAPI]
[AllowNonActiveOrganizationMembership]
internal sealed class AcceptOrganizationInvitationEndpoint(
    IdentityDomainWriteContext domainWriteContext,
    IClock clock) : Endpoint<AcceptOrganizationInvitationRequest>
{
    public override void Configure()
    {
        Post("api/organization/invitations/accept");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequireEmailVerified();
        Tags("Identity/Organization");
        Summary(summary =>
        {
            summary.Summary = "Accept an organization invitation";
            summary.Description = "Rechecks seat capacity and consumes a single-use invitation when its email matches the authenticated account.";
        });
    }

    public override async Task HandleAsync(AcceptOrganizationInvitationRequest req, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var now = clock.GetCurrentInstant();
        var tokenHash = SecureToken.Hash(req.Token);
        var organizationId = await domainWriteContext.OrganizationInvitations
            .AsNoTracking()
            .Where(invitation => invitation.TokenHash == tokenHash)
            .Select(invitation => (Guid?)invitation.OrganizationId)
            .FirstOrDefaultAsync(ct);
        if (organizationId is null)
        {
            AddError(ErrorResponses.General("organization-invitation-invalid"));
            await Send.ErrorsAsync(400, ct);
            return;
        }

        var organization = await domainWriteContext.Organizations
            .SingleAsync(x => x.Id == organizationId.Value, ct);
        organization.FenceMembershipMutation();

        var invitation = await domainWriteContext.OrganizationInvitations
            .Include(i => i.Role)
            .FirstOrDefaultAsync(i => i.TokenHash == tokenHash, ct);
        if (invitation is null || invitation.AcceptedAt is not null || invitation.CancelledAt is not null)
        {
            AddError(ErrorResponses.General("organization-invitation-invalid"));
            await Send.ErrorsAsync(400, ct);
            return;
        }

        if (invitation.Role is null)
        {
            AddError(ErrorResponses.General("organization-invitation-invalid"));
            await Send.ErrorsAsync(400, ct);
            return;
        }

        if (OrganizationRolePermissions.HasGrantManage(invitation.Role.Permissions))
        {
            AddError(ErrorResponses.General("organization-role-grant-manage-cutover-unavailable"));
            await Send.ErrorsAsync(409, ct);
            return;
        }

        var inviter = await domainWriteContext.OrganizationMembers
            .Include(member => member.RoleAssignments)
                .ThenInclude(assignment => assignment.Role)
            .FirstOrDefaultAsync(member => member.OrganizationId == invitation.OrganizationId
                                           && member.UserId == invitation.InvitedBy,
                ct);
        if (inviter is null
            || (inviter.EffectivePermissions() & Permission.AddUser) != Permission.AddUser
            || !OrganizationRoleAuthorization.CanAssignRole(
                inviter, invitation.Role, allowAdministratorForOwner: false))
        {
            AddError(ErrorResponses.General("organization-invitation-role-unassignable"));
            await Send.ErrorsAsync(409, ct);
            return;
        }

        if (!invitation.CanAccept(now))
        {
            AddError(ErrorResponses.General("organization-invitation-expired"));
            await Send.ErrorsAsync(400, ct);
            return;
        }

        var user = await domainWriteContext.Users.FirstAsync(u => u.Id == userId, ct);
        if (!string.Equals(user.Email, invitation.Email, StringComparison.OrdinalIgnoreCase))
        {
            AddError(ErrorResponses.General("organization-invitation-email-mismatch"));
            await Send.ErrorsAsync(403, ct);
            return;
        }

        if (await domainWriteContext.OrganizationMembers.AnyAsync(
                m => m.OrganizationId == invitation.OrganizationId && m.UserId == userId, ct))
        {
            AddError(ErrorResponses.General("organization-member-exists"));
            await Send.ErrorsAsync(409, ct);
            return;
        }

        var occupiedSeats = await domainWriteContext.OrganizationMembers.CountAsync(
            m => m.OrganizationId == invitation.OrganizationId, ct);
        if (occupiedSeats >= organization.SeatLimit)
        {
            AddError(ErrorResponses.General("organization-seat-limit-reached"));
            await Send.ErrorsAsync(409, ct);
            return;
        }

        invitation.Accept(now);
        domainWriteContext.Add(OrganizationMember.Create(
            invitation.OrganizationId, user.Id, invitation.Role,
            user.DisplayName, user.Email, now));
        try
        {
            await domainWriteContext.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        { SqlState: Palladin.Core.Persistence.PostgresErrorCodes.UniqueViolation })
        {
            domainWriteContext.Clear();
            AddError(ErrorResponses.General("organization-member-exists"));
            await Send.ErrorsAsync(409, ct);
            return;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        { SqlState: Npgsql.PostgresErrorCodes.ForeignKeyViolation })
        {
            domainWriteContext.Clear();
            AddError(ErrorResponses.General("organization-invitation-role-unassignable"));
            await Send.ErrorsAsync(409, ct);
            return;
        }

        await Send.NoContentAsync(ct);
    }
}
