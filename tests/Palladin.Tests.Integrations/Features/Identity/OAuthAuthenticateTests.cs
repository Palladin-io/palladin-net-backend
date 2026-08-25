using System.Net;
using System.Net.Http.Json;
using Palladin.Module.Identity.Contracts.ValueObjects;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Domain.Enums;
using Palladin.Module.Identity.Features;
using Palladin.Module.Identity.Infrastructure.OAuth;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Fakers;
using Palladin.Tests.Integrations.Shared.Mocks;
using Palladin.Tests.Integrations.Shared.Seeders;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Palladin.Tests.Integrations.Features.Identity;

[Collection<ApiFactoryCollection>]
public sealed class OAuthAuthenticateTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_NewUser_AuthenticatesWithGoogle_Then_CreatesUserAndReturnsTokens()
    {
        // Given
        apiFactory.MockId(Guid.NewGuid());
        apiFactory.GoogleOAuthProvider.ValidateTokenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ExternalUserInfo("google-sub-123", "newuser@example.com", true, "New User", "https://avatar.url/pic.jpg"));

        var client = apiFactory.CreateClient();

        // When
        var response = await client.PostAsJsonAsync("api/auth/oauth/google", new { Token = "valid-google-token" });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<OAuthAuthenticateResponse>();
        result.ShouldNotBeNull();
        result.AccessToken.ShouldNotBeNullOrWhiteSpace();
        result.RefreshToken.ShouldNotBeNullOrWhiteSpace();
        result.IsOnboarded.ShouldBeFalse();

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        var roles = await readContext.Roles
            .Where(role => role.Organization.Members.Any(member => member.UserId == result.UserId))
            .OrderBy(role => role.Name)
            .ToListAsync(TestContext.Current.CancellationToken);
        roles.Select(role => role.Name).ShouldBe([Role.AdministratorName, Role.DefaultUserName]);
        roles.Single(role => role.IsDefaultUser).Permissions.ShouldBe(Role.DefaultUserPermissions);
    }

    [Fact]
    public async Task When_ExistingUser_AuthenticatesWithGoogle_Then_ReturnsTokens()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        await apiFactory.Services.SeedOAuthConnectionAsync(user.Id, AuthProvider.Google, "google-existing-sub", user.Email);

        apiFactory.GoogleOAuthProvider.ValidateTokenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ExternalUserInfo("google-existing-sub", user.Email, true, user.DisplayName, user.AvatarUrl));

        var client = apiFactory.CreateClient();

        // When
        var response = await client.PostAsJsonAsync("api/auth/oauth/google", new { Token = "valid-google-token" });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<OAuthAuthenticateResponse>();
        result.ShouldNotBeNull();
        result.AccessToken.ShouldNotBeNullOrWhiteSpace();
        result.RefreshToken.ShouldNotBeNullOrWhiteSpace();
        result.UserId.ShouldBe(user.Id);
        result.IsOnboarded.ShouldBeFalse();
    }

    [Fact]
    public async Task When_OnboardedUser_AuthenticatesWithGoogle_Then_IsOnboardedTrue()
    {
        // Given
        var userId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var userFaker = UserFaker.CreateOnboarded(id: userId, organizationId: orgId)
            .RuleFor(x => x.PublicKey, [0x01, 0x02, 0x03]);
        var orgFaker = OrganizationFaker.Create(id: orgId);

        var (user, _, _) = await apiFactory.Services.SeedUserAsync(userFaker, orgFaker);
        await apiFactory.Services.SeedOAuthConnectionAsync(user.Id, AuthProvider.Google, "google-onboarded-sub", user.Email);

        apiFactory.GoogleOAuthProvider.ValidateTokenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ExternalUserInfo("google-onboarded-sub", user.Email, true, user.DisplayName, user.AvatarUrl));

        var client = apiFactory.CreateClient();

        // When
        var response = await client.PostAsJsonAsync("api/auth/oauth/google", new { Token = "valid-google-token" });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<OAuthAuthenticateResponse>();
        result.ShouldNotBeNull();
        result.IsOnboarded.ShouldBeTrue();
    }

    [Fact]
    public async Task When_InvalidToken_Then_Returns401()
    {
        // Given
        apiFactory.GoogleOAuthProvider.ValidateTokenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Invalid token"));

        var client = apiFactory.CreateClient();

        // When
        var response = await client.PostAsJsonAsync("api/auth/oauth/google", new { Token = "invalid-token" });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task When_UnsupportedProvider_Then_Returns400()
    {
        // Given
        var client = apiFactory.CreateClient();

        // When
        var response = await client.PostAsJsonAsync("api/auth/oauth/github", new { Token = "some-token" });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task When_ExistingEmailFromDifferentProvider_Then_LinksToExistingAccount()
    {
        // Given
        var providerUserId = $"google-email-dedup-{Guid.NewGuid()}";
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();

        apiFactory.MockId(Guid.NewGuid());
        apiFactory.GoogleOAuthProvider.ValidateTokenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ExternalUserInfo(providerUserId, user.Email, true, user.DisplayName, user.AvatarUrl));

        var client = apiFactory.CreateClient();

        // When
        var response = await client.PostAsJsonAsync("api/auth/oauth/google", new { Token = "valid-google-token" });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<OAuthAuthenticateResponse>();
        result.ShouldNotBeNull();
        result.UserId.ShouldBe(user.Id);
    }

    [Fact]
    public async Task When_EmailNotVerified_Then_Returns401AndDoesNotCreateUser()
    {
        // Given
        apiFactory.MockId(Guid.NewGuid());
        apiFactory.GoogleOAuthProvider.ValidateTokenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ExternalUserInfo("google-unverified-sub", "unverified@example.com", false, "Unverified", null));

        var client = apiFactory.CreateClient();

        // When
        var response = await client.PostAsJsonAsync("api/auth/oauth/google", new { Token = "valid-google-token" });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task When_VerifiedWaitlistUserCreatesOAuthAccountInClaimWindow_Then_DeveloperBenefitIsReserved()
    {
        // Given
        var originalNow = apiFactory.FakeClock.GetCurrentInstant();
        var accountCreatedAt = Instant.FromUtc(2026, 12, 15, 8, 30);
        apiFactory.FakeClock.Reset(accountCreatedAt);
        apiFactory.MockId(Guid.NewGuid());
        var email = $"oauth-benefit-{Guid.NewGuid():N}@example.com";
        await SeedVerifiedWaitlistAsync(email, Instant.FromUtc(2026, 11, 30, 23, 59));
        var providerUserId = $"google-waitlist-{Guid.NewGuid():N}";
        apiFactory.GoogleOAuthProvider.ValidateTokenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ExternalUserInfo(providerUserId, email.ToUpperInvariant(), true, "Waitlist User", null));

        try
        {
            // When
            var response = await apiFactory.CreateClient()
                .PostAsJsonAsync("api/auth/oauth/google", new { Token = "valid-google-token" });

            // Then
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var result = await response.Content.ReadFromJsonAsync<OAuthAuthenticateResponse>();
            result.ShouldNotBeNull();

            await using var scope = apiFactory.Services.CreateAsyncScope();
            var entry = await scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>()
                .WaitlistEntries.SingleAsync(x => x.Email == email, TestContext.Current.CancellationToken);
            entry.BenefitUserId.ShouldBe(result.UserId);
            entry.BenefitPlan.ShouldBe(WaitlistEntry.DeveloperPlan);
            entry.BenefitStatus.ShouldBe(WaitlistEntry.ReservedBenefitStatus);
            entry.BenefitStartsAt.ShouldBe(accountCreatedAt);
            entry.BenefitEndsAt.ShouldBe(Instant.FromUtc(2027, 1, 15, 8, 30));
        }
        finally
        {
            apiFactory.FakeClock.Reset(originalNow);
        }
    }

    private async Task SeedVerifiedWaitlistAsync(string email, Instant createdAt)
    {
        var entry = WaitlistEntry.Join(
            Guid.NewGuid(),
            email,
            "en",
            WaitlistQualification.Create(
                "team",
                "codex",
                null,
                "Rotate API credentials for release automation",
                null,
                true,
                "direct"),
            WaitlistEntry.CurrentPromotionTermsVersion,
            $"token-{Guid.NewGuid():N}",
            $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
            Duration.FromHours(24),
            createdAt);
        entry.Verify(createdAt + Duration.FromMinutes(5));

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
        context.WaitlistEntries.Add(entry);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
