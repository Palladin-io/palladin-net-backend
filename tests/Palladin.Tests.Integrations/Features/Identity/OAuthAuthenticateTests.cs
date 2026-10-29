using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using Palladin.Core.Security;
using Palladin.Module.Identity.Contracts.Events;
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
using MassTransit.Testing;
using NSubstitute;
using NodaTime;
using FastEndpoints;
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
        var externalIdentity = Guid.NewGuid().ToString("N");
        apiFactory.GoogleOAuthProvider.ValidateTokenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ExternalUserInfo($"google-new-{externalIdentity}", $"new-{externalIdentity}@example.com", true, "New User", "https://avatar.url/pic.jpg"));

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
        var accessTokenClaims = new JwtSecurityTokenHandler().ReadJwtToken(result.AccessToken).Claims;
        result.IsNewUser.ShouldBeTrue();
        accessTokenClaims.Single(claim => claim.Type == JwtClaimNames.OrganizationOfflineAccessPolicy)
            .Value.ShouldBe(((ushort)OrganizationOfflineAccessPolicy.TwentyFourHours).ToString());
        accessTokenClaims.Single(claim => claim.Type == JwtClaimNames.OrganizationOfflineAccessPolicyVersion)
            .Value.ShouldBe("1");

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
        result.IsNewUser.ShouldBeFalse();
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
    public async Task When_GoogleEmailMatchesEmptyUnverifiedPasswordAccount_Then_DiscardsItAndCreatesNewAccount()
    {
        // Given
        apiFactory.GuidProvider.Generate().Returns(_ => Guid.NewGuid());
        var authHash = RandomCredential();
        var email = $"preregistered-{Guid.NewGuid():N}@example.com";
        var (squatter, squatterOrganization) = await apiFactory.Services.SeedPasswordUserAsync(authHash, email: email, emailVerified: false);
        MockGoogleIdentity($"google-victim-{Guid.NewGuid():N}", email);

        // When
        var response = await apiFactory.CreateClient().PostAsJsonAsync(
            "api/auth/oauth/google", new { Token = "valid-google-token" }, TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result = (await response.Content.ReadFromJsonAsync<OAuthAuthenticateResponse>(TestContext.Current.CancellationToken))!;
        result.IsNewUser.ShouldBeTrue();
        result.UserId.ShouldNotBe(squatter.Id);
        await ShouldBeDiscardedAsync(squatter.Id, squatterOrganization.Id, result.UserId);
        (await LoginWithPasswordAsync(email, authHash)).ShouldBe(HttpStatusCode.Unauthorized);
        (await apiFactory.Services.GetRequiredService<ITestHarness>().Published
            .Any<UnverifiedAccountDiscardedEvent>(
                x => x.Context.Message.UserId == squatter.Id && x.Context.Message.OrganizationId == squatterOrganization.Id,
                TestContext.Current.CancellationToken))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task When_BrowserGoogleEmailMatchesEmptyUnverifiedPasswordAccount_Then_DiscardsItAndCreatesNewAccount()
    {
        // Given
        apiFactory.GuidProvider.Generate().Returns(_ => Guid.NewGuid());
        var email = $"preregistered-browser-{Guid.NewGuid():N}@example.com";
        var (squatter, squatterOrganization) = await apiFactory.Services.SeedPasswordUserAsync(RandomCredential(), email: email, emailVerified: false);
        MockGoogleIdentity($"google-browser-victim-{Guid.NewGuid():N}", email);
        using var client = apiFactory.CreateClient();
        client.DefaultRequestHeaders.Add("Origin", "https://panel.example.test");
        client.DefaultRequestHeaders.Add("X-Palladin-Browser", "1");

        // When
        var response = await client.PostAsJsonAsync(
            "api/browser/auth/oauth/google", new { token = "valid-google-token" }, TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var newUserId = await scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>().Users
            .Where(u => u.Email == email).Select(u => u.Id).SingleAsync(TestContext.Current.CancellationToken);
        await ShouldBeDiscardedAsync(squatter.Id, squatterOrganization.Id, newUserId);
    }

    [Fact]
    public async Task When_GoogleEmailMatchesUnverifiedAccountWithOtherMembership_Then_Returns401AndChangesNothing()
    {
        // Given
        apiFactory.GuidProvider.Generate().Returns(_ => Guid.NewGuid());
        var authHash = RandomCredential();
        var email = $"preregistered-member-{Guid.NewGuid():N}@example.com";
        var (squatter, _) = await apiFactory.Services.SeedPasswordUserAsync(authHash, email: email, emailVerified: false);
        var (_, otherOrganization, _) = await apiFactory.Services.SeedUserAsync();
        await AddMembershipAsync(otherOrganization.Id, squatter);
        MockGoogleIdentity($"google-member-{Guid.NewGuid():N}", email);

        // When
        var response = await apiFactory.CreateClient().PostAsJsonAsync(
            "api/auth/oauth/google", new { Token = "valid-google-token" }, TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        (await readContext.Users.SingleAsync(u => u.Email == email, TestContext.Current.CancellationToken)).Id.ShouldBe(squatter.Id);
        (await readContext.OAuthConnections.AnyAsync(c => c.UserId == squatter.Id, TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await LoginWithPasswordAsync(email, authHash)).ShouldBe(HttpStatusCode.OK);
    }

    private void MockGoogleIdentity(string subject, string email) =>
        apiFactory.GoogleOAuthProvider.ValidateTokenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ExternalUserInfo(subject, email, true, "Google Victim", null));

    private static byte[] RandomCredential() => System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

    private async Task ShouldBeDiscardedAsync(Guid oldUserId, Guid oldOrganizationId, Guid newUserId)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        (await readContext.Users.AnyAsync(u => u.Id == oldUserId, TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await readContext.Organizations.AnyAsync(o => o.Id == oldOrganizationId, TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await readContext.OrganizationMembers.AnyAsync(m => m.UserId == newUserId && m.OrganizationId == oldOrganizationId, TestContext.Current.CancellationToken))
            .ShouldBeFalse();
        (await readContext.PasswordCredentials.AnyAsync(c => c.UserId == newUserId, TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    private async Task<HttpStatusCode> LoginWithPasswordAsync(string email, byte[] authHash)
    {
        var (response, _) = await apiFactory.CreateClient().POSTAsync<LoginEndpoint, LoginRequest, LoginResponse>(
            new LoginRequest { Email = email, SecurityVersion = 1, KdfProfileId = "identity-argon2id-password-v1", AuthCredential = authHash });
        return response.StatusCode;
    }

    private async Task AddMembershipAsync(Guid organizationId, User user)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
        var role = (await writeContext.Roles.Where(r => r.OrganizationId == organizationId)
            .ToListAsync(TestContext.Current.CancellationToken)).Single(r => r.IsDefaultUser);
        writeContext.OrganizationMembers.Add(OrganizationMember.Create(
            organizationId, user.Id, role, user.DisplayName, user.Email, SystemClock.Instance.GetCurrentInstant()));
        await writeContext.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
