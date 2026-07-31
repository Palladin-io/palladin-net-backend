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
        var invitation = await domainWriteContext.OrganizationInvitations
            .Include(i => i.Role)
            .FirstOrDefaultAsync(i => i.TokenHash == SecureToken.Hash(req.Token), ct);
        if (invitation is null || invitation.AcceptedAt is not null)
        {
            AddError(ErrorResponses.General("organization-invitation-invalid"));
            await Send.ErrorsAsync(400, ct);
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

        await using var transaction = await domainWriteContext.BeginTransactionAsync(ct);
        var organization = await domainWriteContext
            .FromSqlInterpolated<Organization>($"""SELECT * FROM "Organizations" WHERE "Id" = {invitation.OrganizationId} FOR UPDATE""")
            .SingleAsync(ct);
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
            AddError(ErrorResponses.General("organization-member-exists"));
            await Send.ErrorsAsync(409, ct);
            return;
        }

        await transaction.CommitAsync(ct);
        await Send.NoContentAsync(ct);
    }
}
