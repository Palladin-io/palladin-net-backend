using Palladin.Core.Api;
using Palladin.Core.Persistence;
using Palladin.Core.Security;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure;
using Palladin.Module.Identity.Infrastructure.Jwt;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Infrastructure.Waitlist;
using Palladin.Module.Identity.Shared;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;

using Microsoft.AspNetCore.Http;

namespace Palladin.Module.Identity.Features;

internal sealed class AcceptOrganizationInvitationOperation(
    IdentityDomainWriteContext domainWriteContext,
    IAuthSessionIssuer sessionIssuer,
    WaitlistDeveloperBenefitActivator waitlistDeveloperBenefitActivator,
    IClock clock)
{
    public async Task<IdentityOperationResult<AuthSessionResponse>> ExecuteAsync(AcceptOrganizationInvitationRequest req, HttpContext httpContext, CancellationToken ct)
    {
        var userId = httpContext.User.GetUserId();
        if (userId is null)
        {
            return IdentityOperationResult<AuthSessionResponse>.Status(401);
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
            return IdentityOperationResult<AuthSessionResponse>.Failure(400, ErrorResponses.General("organization-invitation-invalid"));
        }

        var organization = await domainWriteContext.Organizations
            .SingleAsync(x => x.Id == organizationId.Value, ct);
        organization.FenceMembershipMutation();

        var invitation = await domainWriteContext.OrganizationInvitations
            .Include(i => i.Role)
            .FirstOrDefaultAsync(i => i.TokenHash == tokenHash, ct);
        if (invitation is null || invitation.AcceptedAt is not null || invitation.CancelledAt is not null)
        {
            return IdentityOperationResult<AuthSessionResponse>.Failure(400, ErrorResponses.General("organization-invitation-invalid"));
        }

        if (OrganizationRolePermissions.HasGrantManage(invitation.Role.Permissions))
        {
            return IdentityOperationResult<AuthSessionResponse>.Failure(409, ErrorResponses.General("organization-role-grant-manage-cutover-unavailable"));
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
            return IdentityOperationResult<AuthSessionResponse>.Failure(409, ErrorResponses.General("organization-invitation-role-unassignable"));
        }

        if (!invitation.CanAccept(now))
        {
            return IdentityOperationResult<AuthSessionResponse>.Failure(400, ErrorResponses.General("organization-invitation-expired"));
        }

        var user = await domainWriteContext.Users.FirstAsync(u => u.Id == userId, ct);
        if (!string.Equals(user.Email, invitation.Email, StringComparison.OrdinalIgnoreCase))
        {
            return IdentityOperationResult<AuthSessionResponse>.Failure(403, ErrorResponses.General("organization-invitation-email-mismatch"));
        }

        if (await domainWriteContext.OrganizationMembers.AnyAsync(
                m => m.OrganizationId == invitation.OrganizationId && m.UserId == userId, ct))
        {
            return IdentityOperationResult<AuthSessionResponse>.Failure(409, ErrorResponses.General("organization-member-exists"));
        }

        var occupiedSeats = await domainWriteContext.OrganizationMembers.CountAsync(
            m => m.OrganizationId == invitation.OrganizationId, ct);
        if (occupiedSeats >= organization.SeatLimit)
        {
            return IdentityOperationResult<AuthSessionResponse>.Failure(409, ErrorResponses.General("organization-seat-limit-reached"));
        }

        var previousAuthorizationVersion = await domainWriteContext.RefreshTokens
            .AsNoTracking()
            .Where(token => token.OrganizationId == invitation.OrganizationId && token.UserId == user.Id)
            .MaxAsync(token => (uint?)token.AuthorizationVersion, ct) ?? 0u;
        var authorizationVersion = checked(previousAuthorizationVersion + 1u);

        await using var transaction = await domainWriteContext.BeginTransactionAsync(ct);
        await waitlistDeveloperBenefitActivator.TryActivateAsync(user, now, ct);
        invitation.Accept(now);
        var member = OrganizationMember.Create(
            invitation.OrganizationId, user.Id, invitation.Role,
            user.DisplayName, user.Email, now, authorizationVersion);
        domainWriteContext.Add(member);
        var directoryEntry = await domainWriteContext.OrganizationMemberDirectoryEntries
            .SingleOrDefaultAsync(entry => entry.OrganizationId == invitation.OrganizationId
                                           && entry.UserId == user.Id, ct);
        if (directoryEntry is null)
        {
            domainWriteContext.Add(OrganizationMemberDirectoryEntry.Create(
                invitation.OrganizationId, user.Id, user.DisplayName, now));
        }
        else
        {
            directoryEntry.Refresh(user.DisplayName, now);
        }
        var (accessToken, refreshToken) = sessionIssuer.Issue(
            user,
            organization,
            member.EffectivePermissions(),
            member.AuthorizationVersion,
            now);
        try
        {
            await domainWriteContext.CommitAsync(transaction, ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        { SqlState: Palladin.Core.Persistence.PostgresErrorCodes.UniqueViolation })
        {
            domainWriteContext.Clear();
            return IdentityOperationResult<AuthSessionResponse>.Failure(409, ErrorResponses.General("organization-member-exists"));
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        { SqlState: Npgsql.PostgresErrorCodes.ForeignKeyViolation })
        {
            domainWriteContext.Clear();
            return IdentityOperationResult<AuthSessionResponse>.Failure(409, ErrorResponses.General("organization-invitation-role-unassignable"));
        }

        return IdentityOperationResult<AuthSessionResponse>.Ok(new AuthSessionResponse(
            accessToken,
            refreshToken,
            user.Id,
            user.IsOnboarded,
            user.EmailVerified,
            user.ActiveWaitlistDeveloperBenefitStartedAt(now),
            user.ActiveWaitlistDeveloperBenefitEndsAt(now)));
    }
}
