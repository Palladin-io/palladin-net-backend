using Palladin.Core.Api;
using Palladin.Core.Guid;
using Palladin.Core.Security;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure;
using Palladin.Module.Identity.Infrastructure.Invitations;
using Palladin.Module.Identity.Infrastructure.Persistence;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record InviteOrganizationMemberRequest
{
    public string Email { get; init; } = string.Empty;
    public Guid RoleId { get; init; }
}

[UsedImplicitly]
internal sealed class InviteOrganizationMemberValidator : Validator<InviteOrganizationMemberRequest>
{
    public InviteOrganizationMemberValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.RoleId).NotEmpty();
    }
}

[PublicAPI]
internal sealed class InviteOrganizationMemberEndpoint(
    IdentityDomainWriteContext domainWriteContext,
    IGuidProvider guidProvider,
    IOptions<OrganizationInvitationOptions> options,
    IClock clock) : Endpoint<InviteOrganizationMemberRequest>
{
    public override void Configure()
    {
        Post("api/organization/invitations");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.AddUser);
        this.RequireEmailVerified();
        Tags("Identity/Organization");
        Summary(summary =>
        {
            summary.Summary = "Invite an organization member";
            summary.Description = "Reserves an organization seat, creates a single-use hashed token, and sends the invitation through Notification/SES.";
        });
    }

    public override async Task HandleAsync(InviteOrganizationMemberRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId();
        var invitedBy = User.GetUserId();
        if (organizationId is null || invitedBy is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var email = req.Email.Trim().ToLowerInvariant();
        var now = clock.GetCurrentInstant();
        var inviter = await domainWriteContext.Users.FirstAsync(u => u.Id == invitedBy, ct);
        var role = await domainWriteContext.Roles.FirstOrDefaultAsync(
            r => r.OrganizationId == organizationId && r.Id == req.RoleId, ct);
        if (role is null)
        {
            AddError(ErrorResponses.General("organization-role-invalid"));
            await Send.ErrorsAsync(400, ct);
            return;
        }

        var alreadyMember = await domainWriteContext.OrganizationMembers
            .AnyAsync(m => m.OrganizationId == organizationId && m.User.Email == email, ct);
        if (alreadyMember)
        {
            AddError(ErrorResponses.General("organization-member-exists"));
            await Send.ErrorsAsync(409, ct);
            return;
        }

        var pendingInvitation = await domainWriteContext.OrganizationInvitations.AnyAsync(
            i => i.OrganizationId == organizationId && i.Email == email
                && i.AcceptedAt == null && i.ExpiresAt > now, ct);
        if (pendingInvitation)
        {
            AddError(ErrorResponses.General("organization-invitation-pending"));
            await Send.ErrorsAsync(409, ct);
            return;
        }

        await using var transaction = await domainWriteContext.BeginTransactionAsync(ct);
        var organization = await domainWriteContext
            .FromSqlInterpolated<Organization>($"""SELECT * FROM "Organizations" WHERE "Id" = {organizationId.Value} FOR UPDATE""")
            .SingleAsync(ct);
        if (await domainWriteContext.OrganizationMembers.AnyAsync(
                m => m.OrganizationId == organizationId && m.User.Email == email, ct))
        {
            AddError(ErrorResponses.General("organization-member-exists"));
            await Send.ErrorsAsync(409, ct);
            return;
        }

        if (await domainWriteContext.OrganizationInvitations.AnyAsync(
                i => i.OrganizationId == organizationId && i.Email == email
                    && i.AcceptedAt == null && i.ExpiresAt > now, ct))
        {
            AddError(ErrorResponses.General("organization-invitation-pending"));
            await Send.ErrorsAsync(409, ct);
            return;
        }

        var occupiedSeats = await domainWriteContext.OrganizationMembers.CountAsync(
            m => m.OrganizationId == organizationId, ct);
        var reservedSeats = await domainWriteContext.OrganizationInvitations.CountAsync(
            i => i.OrganizationId == organizationId
                && i.AcceptedAt == null && i.ExpiresAt > now, ct);
        if (occupiedSeats + reservedSeats >= organization.SeatLimit)
        {
            AddError(ErrorResponses.General("organization-seat-limit-reached"));
            await Send.ErrorsAsync(409, ct);
            return;
        }

        var (token, tokenHash) = SecureToken.Generate();
        var ttl = Duration.FromHours(options.Value.TokenTtlHours);
        var invitationId = guidProvider.Generate();
        domainWriteContext.Add(OrganizationInvitation.Create(
            invitationId, organizationId.Value, organization.Name, role.Id, role.Name,
            invitedBy.Value, inviter.DisplayName, email, inviter.PreferredLanguage.Code,
            token, tokenHash, ttl, now));
        await domainWriteContext.CommitAsync(ct);
        await transaction.CommitAsync(ct);

        await Send.NoContentAsync(ct);
    }
}
