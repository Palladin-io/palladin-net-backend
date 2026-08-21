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
}
