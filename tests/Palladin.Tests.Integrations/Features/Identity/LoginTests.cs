using System.Net;
using System.Text;
using Palladin.Module.Identity.Features;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Seeders;
using FastEndpoints;
using NSubstitute;
using OtpNet;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Identity;

[Collection<ApiFactoryCollection>]
public sealed class LoginTests(ApiFactory apiFactory) : TestBase
{
    [Theory]
    [InlineData("null")]
    [InlineData("123")]
    [InlineData("{}")]
    public async Task When_AuthCredentialJsonIsMalformed_Then_RequestReturnsValidationError(string value)
    {
        var content = new StringContent(
            $$"""{"email":"user@example.com","securityVersion":3,"kdfProfileId":"identity-argon2id-password-v1","authCredential":{{value}}}""",
            Encoding.UTF8,
            "application/json");

        var response = await apiFactory.CreateClient().PostAsync("api/auth/login", content);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task When_LoginWithCorrectAuthHash_Then_IssuesSession()
    {
        // Given
        var authHash = RandomBytes(32);
        var email = $"login-{Guid.NewGuid():N}@example.com";
        await apiFactory.Services.SeedPasswordUserAsync(authHash, email: email, emailVerified: true);
        var client = apiFactory.CreateClient();

        // When
        var (response, result) = await client.POSTAsync<LoginEndpoint, LoginRequest, LoginResponse>(
            new LoginRequest { Email = email, SecurityVersion = 1, KdfProfileId = "identity-argon2id-password-v1", AuthCredential = authHash });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.TotpRequired.ShouldBeFalse();
        result.AccessToken.ShouldNotBeNullOrWhiteSpace();
        result.EmailVerified.ShouldBe(true);
    }

    [Fact]
    public async Task When_LoginWithWrongAuthHash_Then_Returns401()
    {
        // Given
        var email = $"login-{Guid.NewGuid():N}@example.com";
        await apiFactory.Services.SeedPasswordUserAsync(RandomBytes(32), email: email);
        var client = apiFactory.CreateClient();

        // When
        var (response, _) = await client.POSTAsync<LoginEndpoint, LoginRequest, LoginResponse>(
            new LoginRequest { Email = email, SecurityVersion = 1, KdfProfileId = "identity-argon2id-password-v1", AuthCredential = RandomBytes(32) });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task When_LoginForUnknownEmail_Then_Returns401()
    {
        // Given
        var client = apiFactory.CreateClient();

        // When
        var (response, _) = await client.POSTAsync<LoginEndpoint, LoginRequest, LoginResponse>(
            new LoginRequest
            {
                Email = $"nobody-{Guid.NewGuid():N}@example.com",
                SecurityVersion = 1,
                KdfProfileId = "identity-argon2id-password-v1",
                AuthCredential = RandomBytes(32),
            });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task When_RepeatedFailures_Then_LocksOutWith429()
    {
        // Given — MaxAttempts is 5; the sixth attempt must be locked out.
        apiFactory.GuidProvider.Generate().Returns(_ => Guid.NewGuid());
        var email = $"lock-{Guid.NewGuid():N}@example.com";
        await apiFactory.Services.SeedPasswordUserAsync(RandomBytes(32), email: email);
        var client = apiFactory.CreateClient();
        var badRequest = new LoginRequest { Email = email, SecurityVersion = 1, KdfProfileId = "identity-argon2id-password-v1", AuthCredential = RandomBytes(32) };

        // When
        for (var i = 0; i < 5; i++)
        {
            await client.POSTAsync<LoginEndpoint, LoginRequest, LoginResponse>(badRequest);
        }

        var (response, _) = await client.POSTAsync<LoginEndpoint, LoginRequest, LoginResponse>(badRequest);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task When_LoginWithTotpEnabled_Then_ReturnsChallengeInsteadOfSession()
    {
        // Given
        var authHash = RandomBytes(32);
        var email = $"totp-{Guid.NewGuid():N}@example.com";
        var (user, _) = await apiFactory.Services.SeedPasswordUserAsync(authHash, email: email, emailVerified: true);
        apiFactory.GuidProvider.Generate().Returns(_ => Guid.NewGuid());
        await apiFactory.Services.SeedTotpCredentialAsync(user.Id, Base32Encoding.ToString(KeyGeneration.GenerateRandomKey(20)));
        var client = apiFactory.CreateClient();

        // When
        var (response, result) = await client.POSTAsync<LoginEndpoint, LoginRequest, LoginResponse>(
            new LoginRequest { Email = email, SecurityVersion = 1, KdfProfileId = "identity-argon2id-password-v1", AuthCredential = authHash });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.TotpRequired.ShouldBeTrue();
        result.ChallengeToken.ShouldNotBeNullOrWhiteSpace();
        result.AccessToken.ShouldBeNull();
    }

    private static byte[] RandomBytes(int length) => KeyGeneration.GenerateRandomKey(length);
}
