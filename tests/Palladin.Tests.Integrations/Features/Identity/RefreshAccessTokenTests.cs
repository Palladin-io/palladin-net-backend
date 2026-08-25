using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Palladin.Core.Security;
using Palladin.Module.Identity.Contracts.ValueObjects;
using Palladin.Module.Identity.Features;
using Palladin.Module.Identity.Infrastructure.Jwt;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Fakers;
using Palladin.Tests.Integrations.Shared.Seeders;
using NodaTime;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Identity;

[Collection<ApiFactoryCollection>]
public sealed class RefreshAccessTokenTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_RefreshTokenSurvivesBulkRevocationRace_Then_AuthorizationVersionFenceRejectsIt()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var rawToken = Convert.ToBase64String(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
        var staleToken = await apiFactory.Services.SeedRefreshTokenAsync(user.Id, rawToken);

        await using (var mutationScope = apiFactory.Services.CreateAsyncScope())
        {
            var writeContext = mutationScope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
            var membership = await writeContext.OrganizationMembers.SingleAsync(member =>
                member.OrganizationId == organization.Id && member.UserId == user.Id);
            membership.InvalidateAuthorization(apiFactory.FakeClock.GetCurrentInstant());
            membership.FetchEvents();
            await writeContext.SaveChangesAsync();
        }

        var response = await apiFactory.CreateClient().PostAsJsonAsync(
            "api/auth/refresh",
            new { RefreshToken = rawToken },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await using var verificationScope = apiFactory.Services.CreateAsyncScope();
        var persistedToken = await verificationScope.ServiceProvider
            .GetRequiredService<IdentityDbReadContext>()
            .RefreshTokens.SingleAsync(token => token.Id == staleToken.Id);
        persistedToken.AuthorizationVersion.ShouldBe(1u);
        persistedToken.RevokedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task When_RefreshingForProOrg_Then_AccessTokenCarriesProPlanClaim()
    {
        // Given
        var proOrg = OrganizationFaker.Create().RuleFor(x => x.PlanType, PlanType.Pro);
        var (user, _, _) = await apiFactory.Services.SeedUserAsync(organizationFaker: proOrg);
        var rawToken = Convert.ToBase64String(new byte[] { 9, 9, 9, 9, 9, 9, 9, 9, 8, 8, 8, 8, 8, 8, 8, 8, 7, 7, 7, 7, 7, 7, 7, 7, 6, 6, 6, 6, 6, 6, 6, 6 });
        await apiFactory.Services.SeedRefreshTokenAsync(user.Id, rawToken);
        var client = apiFactory.CreateClient();

        // When
        var response = await client.PostAsJsonAsync("api/auth/refresh", new { RefreshToken = rawToken });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<RefreshAccessTokenResponse>();
        var plan = new JwtSecurityTokenHandler()
            .ReadJwtToken(result!.AccessToken)
            .Claims.First(c => c.Type == JwtClaimNames.Plan).Value;
        plan.ShouldBe(PlanType.Pro.ToString());
    }

    [Fact]
    public async Task When_RefreshingDuringWaitlistBenefit_Then_DeveloperAccessEndsWithBenefit()
    {
        // Given
        var now = apiFactory.FakeClock.GetCurrentInstant();
        var benefitEndsAt = now + Duration.FromMinutes(15);
        var userFaker = UserFaker.Create()
            .RuleFor(user => user.EmailVerified, true)
            .RuleFor(user => user.IsOnboarded, true)
            .RuleFor(user => user.WaitlistDeveloperBenefitStartedAt, now - Duration.FromDays(1))
            .RuleFor(user => user.WaitlistDeveloperBenefitEndsAt, benefitEndsAt);
        var (user, _, _) = await apiFactory.Services.SeedUserAsync(userFaker: userFaker);
        var rawToken = Convert.ToBase64String(
            Enumerable.Range(100, 32).Select(value => (byte)value).ToArray());
        await apiFactory.Services.SeedRefreshTokenAsync(user.Id, rawToken);

        // When
        var response = await apiFactory.CreateClient().PostAsJsonAsync(
            "api/auth/refresh",
            new { RefreshToken = rawToken },
            TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await using var responseStream = await response.Content.ReadAsStreamAsync(
            TestContext.Current.CancellationToken);
        using var result = await JsonDocument.ParseAsync(
            responseStream,
            cancellationToken: TestContext.Current.CancellationToken);
        var responseRoot = result.RootElement;
        responseRoot.GetProperty("userId").GetGuid().ShouldBe(user.Id);
        responseRoot.GetProperty("waitlistDeveloperBenefitStartedAt").GetDateTimeOffset()
            .ShouldBe((now - Duration.FromDays(1)).ToDateTimeOffset(), TimeSpan.FromMilliseconds(1));
        responseRoot.GetProperty("waitlistDeveloperBenefitEndsAt").GetDateTimeOffset()
            .ShouldBe(benefitEndsAt.ToDateTimeOffset(), TimeSpan.FromMilliseconds(1));

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(
            responseRoot.GetProperty("accessToken").GetString());
        jwt.Claims.First(claim => claim.Type == JwtClaimNames.Plan).Value
            .ShouldBe(PlanType.Pro.ToString());
        jwt.ValidTo.ShouldBe(benefitEndsAt.ToDateTimeUtc(), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task When_ValidRefreshToken_NotCloseToExpiry_Then_ReturnsSameRefreshToken()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var rawToken = Convert.ToBase64String(new byte[32]);
        await apiFactory.Services.SeedRefreshTokenAsync(user.Id, rawToken);

        var client = apiFactory.CreateClient();

        // When
        var response = await client.PostAsJsonAsync("api/auth/refresh", new { RefreshToken = rawToken });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<RefreshAccessTokenResponse>();
        result.ShouldNotBeNull();
        result.AccessToken.ShouldNotBeNullOrWhiteSpace();
        result.RefreshToken.ShouldBe(rawToken);
    }

    [Fact]
    public async Task When_ValidRefreshToken_CloseToExpiry_Then_RotatesAndReturnsNewTokens()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var rawToken = Convert.ToBase64String(new byte[32]);
        var closeToExpiry = SystemClock.Instance.GetCurrentInstant().Plus(Duration.FromDays(3));
        await apiFactory.Services.SeedRefreshTokenAsync(user.Id, rawToken, expiresAt: closeToExpiry);

        var client = apiFactory.CreateClient();

        // When
        var response = await client.PostAsJsonAsync("api/auth/refresh", new { RefreshToken = rawToken });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<RefreshAccessTokenResponse>();
        result.ShouldNotBeNull();
        result.AccessToken.ShouldNotBeNullOrWhiteSpace();
        result.RefreshToken.ShouldNotBeNullOrWhiteSpace();
        result.RefreshToken.ShouldNotBe(rawToken);
    }

    [Fact]
    public async Task When_ExpiredRefreshToken_Then_Returns401()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var rawToken = Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32 });
        var expiredAt = SystemClock.Instance.GetCurrentInstant().Minus(Duration.FromDays(1));
        await apiFactory.Services.SeedRefreshTokenAsync(user.Id, rawToken, expiresAt: expiredAt);

        var client = apiFactory.CreateClient();

        // When
        var response = await client.PostAsJsonAsync("api/auth/refresh", new { RefreshToken = rawToken });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task When_RevokedTokenReplayed_Then_RevokesLineageButNotOtherSessions()
    {
        // Given — a rotation lineage (revoked parent → active child) an attacker captured the parent of,
        // plus an unrelated active token from a different login session that must stay valid
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();

        var childId = Guid.NewGuid();
        var childRaw = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)(i + 50)).ToArray());
        await apiFactory.Services.SeedRefreshTokenAsync(user.Id, childRaw, id: childId);

        var replayedRaw = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
        await apiFactory.Services.SeedRefreshTokenAsync(
            user.Id,
            replayedRaw,
            revokedAt: SystemClock.Instance.GetCurrentInstant().Minus(Duration.FromHours(1)),
            replacedByTokenId: childId);

        var otherSessionRaw = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)(i + 100)).ToArray());
        await apiFactory.Services.SeedRefreshTokenAsync(user.Id, otherSessionRaw);

        var client = apiFactory.CreateClient();

        // When — the attacker replays the already-rotated parent token
        var response = await client.PostAsJsonAsync("api/auth/refresh", new { RefreshToken = replayedRaw });

        // Then — rejected, the active child in the same lineage is revoked, the other session survives
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var childResponse = await client.PostAsJsonAsync("api/auth/refresh", new { RefreshToken = childRaw });
        childResponse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var otherSessionResponse = await client.PostAsJsonAsync("api/auth/refresh", new { RefreshToken = otherSessionRaw });
        otherSessionResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task When_RevokedRefreshToken_Then_Returns401()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var rawToken = Convert.ToBase64String(new byte[] { 32, 31, 30, 29, 28, 27, 26, 25, 24, 23, 22, 21, 20, 19, 18, 17, 16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1 });
        var revokedAt = SystemClock.Instance.GetCurrentInstant().Minus(Duration.FromHours(1));
        await apiFactory.Services.SeedRefreshTokenAsync(user.Id, rawToken, revokedAt: revokedAt);

        var client = apiFactory.CreateClient();

        // When
        var response = await client.PostAsJsonAsync("api/auth/refresh", new { RefreshToken = rawToken });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
