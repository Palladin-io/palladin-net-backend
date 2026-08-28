using Palladin.Core.Security;
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
using Palladin.Module.Identity.Domain;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record SwitchOrganizationRequest
{
    public Guid OrganizationId { get; init; }
}

[UsedImplicitly]
internal sealed class SwitchOrganizationValidator : Validator<SwitchOrganizationRequest>
{
    public SwitchOrganizationValidator() => RuleFor(x => x.OrganizationId).NotEmpty();
}

[PublicAPI]
internal sealed class SwitchOrganizationEndpoint(
    IdentityDomainWriteContext domainWriteContext,
    IAuthSessionIssuer sessionIssuer,
    WaitlistDeveloperBenefitActivator waitlistDeveloperBenefitActivator,
    IClock clock) : Endpoint<SwitchOrganizationRequest, AuthSessionResponse>
{
    public override void Configure()
    {
        Post("api/auth/switch-organization");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Options(builder => builder.AllowNonActiveOrganizationMembership());
        this.RequireEmailVerified();
        Tags("Identity/Auth");
        Summary(summary =>
        {
            summary.Summary = "Switch the active organization";
            summary.Description = "Issues a new session scoped to an organization the authenticated user belongs to.";
        });
    }

    public override async Task HandleAsync(SwitchOrganizationRequest req, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var member = await domainWriteContext.OrganizationMembers
            .Include(m => m.User)
            .Include(m => m.RoleAssignments)
                .ThenInclude(assignment => assignment.Role)
            .Include(m => m.Organization)
            .FirstOrDefaultAsync(m => m.OrganizationId == req.OrganizationId && m.UserId == userId, ct);
        if (member is null || member.Status != OrganizationMemberStatus.Active)
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        var permissions = member.EffectivePermissions();
        var now = clock.GetCurrentInstant();
        await using var transaction = await domainWriteContext.BeginTransactionAsync(ct);
        await waitlistDeveloperBenefitActivator.TryActivateAsync(member.User, now, ct);
        var (accessToken, refreshToken) = sessionIssuer.Issue(
            member.User,
            member.OrganizationId,
            permissions,
            member.Organization.PlanType,
            member.AuthorizationVersion,
            now);
        await domainWriteContext.CommitAsync(transaction, ct);

        await Send.OkAsync(new AuthSessionResponse(
            accessToken,
            refreshToken,
            member.UserId,
            member.User.IsOnboarded,
            member.User.EmailVerified,
            member.User.ActiveWaitlistDeveloperBenefitStartedAt(now),
            member.User.ActiveWaitlistDeveloperBenefitEndsAt(now)), ct);
    }
}
