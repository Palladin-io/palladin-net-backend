using Palladin.Core.Guid;
using Palladin.Core.Security;
using Palladin.Core.Transport;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Contracts.ValueObjects;
using Palladin.Module.Identity.Domain.Enums;
using Palladin.Module.Identity.Infrastructure.Jwt;
using Palladin.Module.Identity.Infrastructure.OAuth;
using Palladin.Module.Identity.Infrastructure.Options;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Infrastructure.Waitlist;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using Serilog;

using Microsoft.AspNetCore.Http;
using Palladin.Module.Identity.Shared;

namespace Palladin.Module.Identity.Features;

internal sealed class OAuthAuthenticateOperation(
    IEnumerable<IExternalOAuthProvider> oauthProviders,
    IdentityDomainWriteContext domainWriteContext,
    ITokenService tokenService,
    IGuidProvider guidProvider,
    IClock clock,
    IOptions<JwtOptions> jwtOptions,
    ITransportContext transportContext,
    WaitlistDeveloperBenefitActivator waitlistDeveloperBenefitActivator,
    ILogger logger)
{
    public async Task<IdentityOperationResult<OAuthAuthenticateResponse>> ExecuteAsync(OAuthAuthenticateRequest req, HttpContext httpContext, CancellationToken ct)
    {
        var provider = oauthProviders.FirstOrDefault(
            p => p.Provider.ToString().Equals(req.Provider, StringComparison.OrdinalIgnoreCase));

        if (provider is null)
        {
            return IdentityOperationResult<OAuthAuthenticateResponse>.Failure(400, "Unsupported provider");
        }

        ExternalUserInfo externalUser;
        try
        {
            externalUser = await provider.ValidateTokenAsync(req.Token, ct);
        }
        catch (Exception exception)
        {
            logger.Error(exception, "An error occurred while validating the token");

            return IdentityOperationResult<OAuthAuthenticateResponse>.Status(401);
        }

        // An unverified email must never auto-link or create an account — an attacker could claim someone else's.
        if (!externalUser.EmailVerified)
        {
            logger.Warning("Rejected OAuth sign-in for provider {Provider}: email not verified", provider.Provider);
            return IdentityOperationResult<OAuthAuthenticateResponse>.Status(401);
        }

        externalUser = externalUser with { Email = externalUser.Email.Trim().ToLowerInvariant() };

        var now = clock.GetCurrentInstant();
        var platform = transportContext.Platform ?? "unknown";

        await using var transaction = await domainWriteContext.BeginTransactionAsync(ct);
        // A (provider, subject) connection match takes precedence over an email-only match.
        var user = await domainWriteContext.Users
            .Include(u => u.OrganizationMemberships)
                .ThenInclude(m => m.RoleAssignments)
                    .ThenInclude(assignment => assignment.Role)
            .Include(u => u.OAuthConnections)
            .Include(u => u.Organization)
            .Where(u => u.OAuthConnections.Any(
                    c => c.Provider == provider.Provider && c.ProviderUserId == externalUser.SubjectId)
                || u.Email == externalUser.Email)
            .OrderByDescending(u => u.OAuthConnections.Any(
                c => c.Provider == provider.Provider && c.ProviderUserId == externalUser.SubjectId))
            .FirstOrDefaultAsync(ct);

        bool isNewUser;
        OrganizationMember? activeMembership = null;
        Organization? createdOrganization = null;

        if (user is not null)
        {
            activeMembership = user.OrganizationMemberships.SingleOrDefault(
                membership => membership.OrganizationId == user.OrganizationId);
            if (activeMembership is null || activeMembership.Status != OrganizationMemberStatus.Active)
            {
                return IdentityOperationResult<OAuthAuthenticateResponse>.Status(401);
            }

            EnsureOAuthConnectionExists(user, provider.Provider, externalUser, now);
            user.RecordLogin(provider.Provider.ToString(), platform);
            isNewUser = false;
        }
        else
        {
            (user, createdOrganization) = CreateNewUser(provider.Provider, externalUser, platform, now);
            isNewUser = true;
        }

        var permissions = isNewUser
            ? (Permission)int.MaxValue
            : activeMembership!.EffectivePermissions();
        var authorizationVersion = isNewUser
            ? 1u
            : activeMembership!.AuthorizationVersion;

        await waitlistDeveloperBenefitActivator.TryActivateAsync(user, now, ct);

        var organization = isNewUser ? createdOrganization! : user.Organization;
        var organizationPlan = organization.PlanType;
        var plan = user.EffectivePlan(organizationPlan, now);
        var accessTokenExpiresAtCap = organizationPlan < PlanType.Pro
            ? user.ActiveWaitlistDeveloperBenefitEndsAt(now)
            : null;
        var accessToken = tokenService.GenerateAccessToken(
            user,
            user.OrganizationId,
            permissions,
            plan,
            authorizationVersion,
            organization.OfflineAccessPolicy,
            organization.OfflineAccessPolicyVersion,
            accessTokenExpiresAtCap);
        var (rawRefreshToken, refreshTokenHash) = tokenService.GenerateRefreshToken();

        var refreshTokenId = guidProvider.Generate();
        var refreshTokenExpiresAt = now.Plus(Duration.FromDays(jwtOptions.Value.RefreshTokenExpiryDays));
        var refreshToken = RefreshToken.Create(
            refreshTokenId, user.Id, user.OrganizationId, refreshTokenHash,
            authorizationVersion, refreshTokenExpiresAt, now);
        domainWriteContext.Add(refreshToken);

        await domainWriteContext.CommitAsync(transaction, ct);

        return IdentityOperationResult<OAuthAuthenticateResponse>.Ok(new OAuthAuthenticateResponse(
            accessToken,
            rawRefreshToken,
            user.Id,
            user.IsOnboarded,
            user.EmailVerified,
            user.ActiveWaitlistDeveloperBenefitStartedAt(now),
            user.ActiveWaitlistDeveloperBenefitEndsAt(now),
            isNewUser));
    }

    private (User User, Organization Organization) CreateNewUser(
        AuthProvider authProvider,
        ExternalUserInfo externalUser,
        string platform,
        Instant now)
    {
        var orgId = guidProvider.Generate();
        var userId = guidProvider.Generate();
        var displayName = externalUser.Name ?? externalUser.Email;

        var organization = Organization.Create(orgId, $"{externalUser.Name}'s Organization", PlanType.Basic, userId, displayName, now);
        domainWriteContext.Add(organization);

        var roleId = guidProvider.Generate();
        var adminRole = Role.CreateAdministrator(roleId, orgId, now);
        domainWriteContext.Add(adminRole);
        domainWriteContext.Add(Role.CreateDefaultUser(guidProvider.Generate(), orgId, now));

        var user = Domain.User.Create(userId, externalUser.Email, displayName, externalUser.Picture, orgId, now, authProvider.ToString(), platform, adminRole.Permissions);
        domainWriteContext.Add(user);

        domainWriteContext.Add(OrganizationMember.CreateOwner(orgId, userId, adminRole, now));
        domainWriteContext.Add(OrganizationMemberDirectoryEntry.Create(
            orgId, userId, displayName, now));

        var connectionId = guidProvider.Generate();
        var oauthConnection = OAuthConnection.Create(connectionId, userId, authProvider, externalUser.SubjectId, externalUser.Email, now);
        domainWriteContext.Add(oauthConnection);

        return (user, organization);
    }

    private void EnsureOAuthConnectionExists(User user, AuthProvider authProvider, ExternalUserInfo externalUser, Instant now)
    {
        var hasConnection = user.OAuthConnections.Any(
            c => c.Provider == authProvider && c.ProviderUserId == externalUser.SubjectId);

        if (!hasConnection)
        {
            var connectionId = guidProvider.Generate();
            var oauthConnection = OAuthConnection.Create(connectionId, user.Id, authProvider, externalUser.SubjectId, externalUser.Email, now);
            domainWriteContext.Add(oauthConnection);
        }
    }
}
