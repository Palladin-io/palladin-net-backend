using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using Palladin.Core.Api;
using Palladin.Core.Guid;
using Palladin.Core.Security;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure;
using Palladin.Module.Identity.Infrastructure.Invitations;
using Palladin.Module.Identity.Infrastructure.Persistence;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record ResendOrganizationInvitationResponse(
    Instant SentAt,
    Instant ExpiresAt,
    Instant ResendAvailableAt);

[PublicAPI]
internal sealed class ResendOrganizationInvitationEndpoint(
    IdentityDomainWriteContext domainWriteContext,
    IGuidProvider guidProvider,
    IOptions<OrganizationInvitationOptions> options,
    IClock clock)
    : EndpointWithoutRequest<ResendOrganizationInvitationResponse>
{
    public override void Configure()
    {
        Post("api/organization/invitations/{invitationId}/resend");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.AddUser);
        this.RequireEmailVerified();
        Tags("Identity/Organization");
        Summary(summary =>
        {
            summary.Summary = "Resend a pending organization invitation";
            summary.Description = "Rotates the single-use token, renews its expiry, and sends a replacement link without reserving another seat.";
        });
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var invitationId = Route<Guid>("invitationId");
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

        var invitation = await domainWriteContext.OrganizationInvitations
            .FirstOrDefaultAsync(candidate => candidate.OrganizationId == organizationId
                                              && candidate.Id == invitationId,
                ct);
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

        var cooldown = Duration.FromSeconds(options.Value.ResendCooldownSeconds);
        var resendAvailableAt = invitation.LastSentAt + cooldown;
        if (now < resendAvailableAt)
        {
            AddError(ErrorResponses.General("organization-invitation-resend-too-soon"));
            await Send.ErrorsAsync(429, ct);
            return;
        }

        var actor = await domainWriteContext.OrganizationMembers
            .Include(member => member.User)
            .Include(member => member.RoleAssignments)
                .ThenInclude(assignment => assignment.Role)
            .SingleAsync(member => member.OrganizationId == organizationId
                                   && member.UserId == userId,
                ct);
        var role = await domainWriteContext.Roles.FirstOrDefaultAsync(
            candidate => candidate.OrganizationId == organizationId
                         && candidate.Id == invitation.RoleId,
            ct);
        if (role is null)
        {
            AddError(ErrorResponses.General("organization-invitation-role-unassignable"));
            await Send.ErrorsAsync(409, ct);
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

        var (token, tokenHash) = SecureToken.Generate();
        var ttl = Duration.FromHours(options.Value.TokenTtlHours);
        invitation.Resend(
            guidProvider.Generate(),
            organization.Name,
            userId.Value,
            actor.User.DisplayName,
            actor.User.PreferredLanguage.Code,
            token,
            tokenHash,
            ttl,
            now);

        await domainWriteContext.CommitAsync(ct);
        await Send.OkAsync(new ResendOrganizationInvitationResponse(
            now,
            now + ttl,
            now + cooldown), ct);
    }
}
