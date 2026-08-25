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

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record OAuthAuthenticateRequest
{
    public string Provider { get; init; } = string.Empty;
    public string Token { get; init; } = string.Empty;
}

[PublicAPI]
public sealed record OAuthAuthenticateResponse(
    string AccessToken,
    string RefreshToken,
    Guid UserId,
    bool IsOnboarded,
    bool EmailVerified,
    Instant? WaitlistDeveloperBenefitStartedAt,
    Instant? WaitlistDeveloperBenefitEndsAt);

[UsedImplicitly]
internal sealed class OAuthAuthenticateValidator : Validator<OAuthAuthenticateRequest>
{
    public OAuthAuthenticateValidator()
    {
        RuleFor(x => x.Provider).NotEmpty();
        RuleFor(x => x.Token).NotEmpty();
    }
}

[PublicAPI]
internal sealed class OAuthAuthenticateEndpoint(
    IEnumerable<IExternalOAuthProvider> oauthProviders,
    IdentityDomainWriteContext domainWriteContext,
    ITokenService tokenService,
    IGuidProvider guidProvider,
    IClock clock,
    IOptions<JwtOptions> jwtOptions,
    ITransportContext transportContext,
    WaitlistDeveloperBenefitActivator waitlistDeveloperBenefitActivator,
    ILogger logger) : Endpoint<OAuthAuthenticateRequest, OAuthAuthenticateResponse>
{
    public override void Configure()
    {
        Post("api/auth/oauth/{Provider}");
        AllowAnonymous();
        Summary(summary =>
        {
            summary.Summary = "Authenticate via OAuth provider";
            summary.Description = "Authenticates a user via an external OAuth provider (Google, Apple, X). Creates a new account if the user does not exist.";
        });
        Tags("Identity/Auth");
    }

    public override async Task HandleAsync(OAuthAuthenticateRequest req, CancellationToken ct)
    {
        var provider = oauthProviders.FirstOrDefault(
            p => p.Provider.ToString().Equals(req.Provider, StringComparison.OrdinalIgnoreCase));

        if (provider is null)
        {
            AddError("Unsupported provider");
            await Send.ErrorsAsync(400, ct);
            return;
        }

        ExternalUserInfo externalUser;
        try
        {
            externalUser = await provider.ValidateTokenAsync(req.Token, ct);
        }
        catch (Exception exception)
        {
            logger.Error(exception, "An error occurred while validating the token");

            await Send.UnauthorizedAsync(ct);
            return;
        }

        // An unverified email must never auto-link or create an account — an attacker could claim someone else's.
        if (!externalUser.EmailVerified)
        {
            logger.Warning("Rejected OAuth sign-in for provider {Provider}: email not verified", provider.Provider);
            await Send.UnauthorizedAsync(ct);
            return;
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

        if (user is not null)
        {
            activeMembership = user.OrganizationMemberships.SingleOrDefault(
                membership => membership.OrganizationId == user.OrganizationId);
            if (activeMembership is null || activeMembership.Status != OrganizationMemberStatus.Active)
            {
                await Send.UnauthorizedAsync(ct);
                return;
            }

            EnsureOAuthConnectionExists(user, provider.Provider, externalUser, now);
            user.RecordLogin(provider.Provider.ToString(), platform);
            isNewUser = false;
        }
        else
        {
            user = CreateNewUser(provider.Provider, externalUser, platform, now);
            isNewUser = true;
        }

        var permissions = isNewUser
            ? (Permission)int.MaxValue
            : activeMembership!.EffectivePermissions();
        var authorizationVersion = isNewUser
            ? 1u
            : activeMembership!.AuthorizationVersion;

        await waitlistDeveloperBenefitActivator.TryActivateAsync(user, now, ct);

        var organizationPlan = isNewUser ? PlanType.Basic : user.Organization.PlanType;
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
            accessTokenExpiresAtCap);
        var (rawRefreshToken, refreshTokenHash) = tokenService.GenerateRefreshToken();

        var refreshTokenId = guidProvider.Generate();
        var refreshTokenExpiresAt = now.Plus(Duration.FromDays(jwtOptions.Value.RefreshTokenExpiryDays));
        var refreshToken = RefreshToken.Create(
            refreshTokenId, user.Id, user.OrganizationId, refreshTokenHash,
            authorizationVersion, refreshTokenExpiresAt, now);
        domainWriteContext.Add(refreshToken);

        await domainWriteContext.CommitAsync(transaction, ct);

        await Send.OkAsync(new OAuthAuthenticateResponse(
            accessToken,
            rawRefreshToken,
            user.Id,
            user.IsOnboarded,
            user.EmailVerified,
            user.ActiveWaitlistDeveloperBenefitStartedAt(now),
            user.ActiveWaitlistDeveloperBenefitEndsAt(now)), ct);
    }

    private User CreateNewUser(AuthProvider authProvider, ExternalUserInfo externalUser, string platform, Instant now)
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

        var connectionId = guidProvider.Generate();
        var oauthConnection = OAuthConnection.Create(connectionId, userId, authProvider, externalUser.SubjectId, externalUser.Email, now);
        domainWriteContext.Add(oauthConnection);

        return user;
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
