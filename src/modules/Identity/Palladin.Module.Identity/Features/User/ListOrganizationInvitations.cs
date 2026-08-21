using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using Palladin.Core.Security;
using Palladin.Module.Identity.Infrastructure.Invitations;
using Palladin.Module.Identity.Infrastructure.Persistence;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record OrganizationInvitationItem(
    Guid Id,
    string Email,
    Guid? RoleId,
    string RoleName,
    string? InvitedByName,
    Instant CreatedAt,
    Instant SentAt,
    Instant ExpiresAt,
    Instant ResendAvailableAt);

[PublicAPI]
public sealed record ListOrganizationInvitationsResponse(
    IReadOnlyCollection<OrganizationInvitationItem> Items);

[PublicAPI]
internal sealed class ListOrganizationInvitationsEndpoint(
    IdentityDomainReadContext domainReadContext,
    IOptions<OrganizationInvitationOptions> options,
    IClock clock)
    : EndpointWithoutRequest<ListOrganizationInvitationsResponse>
{
    public override void Configure()
    {
        Get("api/organization/invitations");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.AddUser);
        this.RequireEmailVerified();
        Tags("Identity/Organization");
        Summary(summary =>
        {
            summary.Summary = "List pending organization invitations";
            summary.Description = "Returns only non-expired, non-cancelled invitations for the active organization.";
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

        var now = clock.GetCurrentInstant();
        var pendingInvitations = await domainReadContext.OrganizationInvitations
            .Where(invitation => invitation.OrganizationId == organizationId
                                 && invitation.AcceptedAt == null
                                 && invitation.CancelledAt == null
                                 && invitation.ExpiresAt > now)
            .OrderByDescending(invitation => invitation.LastSentAt)
            .Select(invitation => new
            {
                invitation.Id,
                invitation.Email,
                invitation.RoleId,
                invitation.RoleName,
                InvitedByName = domainReadContext.Users
                    .Where(user => user.Id == invitation.InvitedBy)
                    .Select(user => user.DisplayName)
                    .SingleOrDefault(),
                invitation.CreatedAt,
                invitation.LastSentAt,
                invitation.ExpiresAt,
            })
            .ToListAsync(ct);
        var cooldown = Duration.FromSeconds(options.Value.ResendCooldownSeconds);
        var items = pendingInvitations
            .Select(invitation => new OrganizationInvitationItem(
                invitation.Id,
                invitation.Email,
                invitation.RoleId,
                invitation.RoleName,
                invitation.InvitedByName,
                invitation.CreatedAt,
                invitation.LastSentAt,
                invitation.ExpiresAt,
                invitation.LastSentAt + cooldown))
            .ToList();

        await Send.OkAsync(new ListOrganizationInvitationsResponse(items), ct);
    }
}
