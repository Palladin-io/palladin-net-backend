using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Api;
using Palladin.Core.Security;
using Palladin.Module.Identity.Infrastructure.Persistence;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record CancelOrganizationInvitationRequest
{
    public Guid InvitationId { get; init; }
}

[UsedImplicitly]
internal sealed class CancelOrganizationInvitationValidator
    : Validator<CancelOrganizationInvitationRequest>
{
    public CancelOrganizationInvitationValidator() =>
        RuleFor(request => request.InvitationId).NotEmpty();
}

[PublicAPI]
internal sealed class CancelOrganizationInvitationEndpoint(
    IdentityDomainWriteContext domainWriteContext,
    IClock clock)
    : Endpoint<CancelOrganizationInvitationRequest>
{
    public override void Configure()
    {
        Delete("api/organization/invitations/{invitationId}");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.AddUser);
        this.RequireEmailVerified();
        Tags("Identity/Organization");
        Summary(summary =>
        {
            summary.Summary = "Cancel a pending organization invitation";
            summary.Description = "Invalidates the invitation token and immediately releases its reserved organization seat.";
        });
    }

    public override async Task HandleAsync(
        CancelOrganizationInvitationRequest req,
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

        var invitation = await domainWriteContext.OrganizationInvitations
            .FirstOrDefaultAsync(candidate => candidate.OrganizationId == organizationId
                                              && candidate.Id == req.InvitationId,
                ct);
        if (invitation is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (invitation.CancelledAt is not null)
        {
            await Send.NoContentAsync(ct);
            return;
        }

        var now = clock.GetCurrentInstant();
        if (!invitation.IsPending(now))
        {
            AddError(ErrorResponses.General("organization-invitation-not-pending"));
            await Send.ErrorsAsync(409, ct);
            return;
        }

        var cancelledByName = await domainWriteContext.Users
            .Where(user => user.Id == userId)
            .Select(user => user.DisplayName)
            .SingleAsync(ct);
        invitation.Cancel(userId.Value, cancelledByName, now);

        await domainWriteContext.CommitAsync(ct);
        await Send.NoContentAsync(ct);
    }
}
