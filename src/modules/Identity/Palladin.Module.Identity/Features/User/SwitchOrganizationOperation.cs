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

using Microsoft.AspNetCore.Http;

namespace Palladin.Module.Identity.Features;

internal sealed class SwitchOrganizationOperation(
    IdentityDomainWriteContext domainWriteContext,
    IAuthSessionIssuer sessionIssuer,
    WaitlistDeveloperBenefitActivator waitlistDeveloperBenefitActivator,
    IClock clock)
{
    public async Task<IdentityOperationResult<AuthSessionResponse>> ExecuteAsync(SwitchOrganizationRequest req, HttpContext httpContext, CancellationToken ct)
    {
        var userId = httpContext.User.GetUserId();
        if (userId is null)
        {
            return IdentityOperationResult<AuthSessionResponse>.Status(401);
        }

        var member = await domainWriteContext.OrganizationMembers
            .Include(m => m.User)
            .Include(m => m.RoleAssignments)
                .ThenInclude(assignment => assignment.Role)
            .Include(m => m.Organization)
            .FirstOrDefaultAsync(m => m.OrganizationId == req.OrganizationId && m.UserId == userId, ct);
        if (member is null || member.Status != OrganizationMemberStatus.Active)
        {
            return IdentityOperationResult<AuthSessionResponse>.Status(403);
        }

        var permissions = member.EffectivePermissions();
        var now = clock.GetCurrentInstant();
        await using var transaction = await domainWriteContext.BeginTransactionAsync(ct);
        await waitlistDeveloperBenefitActivator.TryActivateAsync(member.User, now, ct);
        var (accessToken, refreshToken) = sessionIssuer.Issue(
            member.User,
            member.Organization,
            permissions,
            member.AuthorizationVersion,
            now);
        await domainWriteContext.CommitAsync(transaction, ct);

        return IdentityOperationResult<AuthSessionResponse>.Ok(new AuthSessionResponse(
            accessToken,
            refreshToken,
            member.UserId,
            member.User.IsOnboarded,
            member.User.EmailVerified,
            member.User.ActiveWaitlistDeveloperBenefitStartedAt(now),
            member.User.ActiveWaitlistDeveloperBenefitEndsAt(now)));
    }
}
