using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using Palladin.Module.Identity.Features;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Seeders;
using FastEndpoints;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Identity;

[Collection<ApiFactoryCollection>]
public sealed class LoginSaltTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_KnownEmail_Then_ReturnsStoredSalt()
    {
        // Given
        var authSalt = Enumerable.Range(0, 16).Select(i => (byte)(i + 9)).ToArray();
        var email = $"salt-{Guid.NewGuid():N}@example.com";
        await apiFactory.Services.SeedPasswordUserAsync(
            new byte[32], authSalt: authSalt, email: email);
        var client = apiFactory.CreateClient();

        // When
        var (response, result) = await client.POSTAsync<LoginSaltEndpoint, LoginSaltRequest, LoginSaltResponse>(
            new LoginSaltRequest { Email = email, ProfileId = "identity-argon2id-password-v1" });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.KdfSalt.ShouldBe(WebEncoders.Base64UrlEncode(authSalt));
        result.AccountId.ShouldNotBeNull();
        result.ProfileId.ShouldBe("identity-argon2id-password-v1");
    }

    [Fact]
    public async Task When_UnknownEmail_Then_ReturnsStablePseudoSaltIndistinguishableFromKnown()
    {
        // Given
        var email = $"ghost-{Guid.NewGuid():N}@example.com";
        var client = apiFactory.CreateClient();

        // When — the same unknown email twice
        var request = new LoginSaltRequest
        {
            Email = email,
            ProfileId = "identity-argon2id-password-v1",
        };
        var (firstResponse, first) = await client.POSTAsync<LoginSaltEndpoint, LoginSaltRequest, LoginSaltResponse>(
            request);
        var (_, second) = await client.POSTAsync<LoginSaltEndpoint, LoginSaltRequest, LoginSaltResponse>(
            request);

        // Then — 200 like a known email, stable, and the same 16-byte length
        firstResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        first.KdfSalt.ShouldBe(second.KdfSalt);
        first.AccountId.ShouldBe(second.AccountId);
        first.AccountId.ShouldNotBeNull();
        first.AccountId!.Value.ToString("N")[12].ShouldBe('4');
        WebEncoders.Base64UrlDecode(first.KdfSalt).Length.ShouldBe(16);
    }

    [Fact]
    public async Task When_CurrentProfileIsRequested_Then_ExactRegisteredParametersAreReturned()
    {
        var kdfSalt = Enumerable.Range(0, 16).Select(index => (byte)(index + 20)).ToArray();
        var email = $"salt-v2-{Guid.NewGuid():N}@example.com";
        var (user, _) = await apiFactory.Services.SeedPasswordUserAsync(
            new byte[32],
            authSalt: kdfSalt,
            email: email,
            securityVersion: 1);

        var (response, result) = await apiFactory.CreateClient()
            .POSTAsync<LoginSaltEndpoint, LoginSaltRequest, LoginSaltResponse>(new()
            {
                Email = email,
                ProfileId = "identity-argon2id-password-v1",
            });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.AccountId.ShouldBe(user.Id);
        result.ProfileId.ShouldBe("identity-argon2id-password-v1");
        result.SecurityVersion.ShouldBe((ushort)1);
        result.KdfSalt.ShouldBe(WebEncoders.Base64UrlEncode(kdfSalt));
        result.MemoryKiB.ShouldBe(32768);
        result.Iterations.ShouldBe(2);
        result.Parallelism.ShouldBe(1);
    }

    [Fact]
    public async Task When_UnsupportedProfileIsRequested_Then_RequestFailsClosed()
    {
        var actualSalt = RandomNumberGenerator.GetBytes(16);
        var email = $"salt-mismatch-{Guid.NewGuid():N}@example.com";
        await apiFactory.Services.SeedPasswordUserAsync(
            new byte[32],
            authSalt: actualSalt,
            email: email,
            securityVersion: 1);

        var (response, _) = await apiFactory.CreateClient()
            .POSTAsync<LoginSaltEndpoint, LoginSaltRequest, LoginSaltResponse>(new()
            {
                Email = email,
                ProfileId = "unsupported-profile",
            });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task When_OAuthAccountHasVersion7Id_Then_ReturnsItsRegisteredKdfBootstrap()
    {
        var actualSalt = RandomNumberGenerator.GetBytes(16);
        var accountId = Guid.CreateVersion7();
        var email = $"invalid-v2-id-{Guid.NewGuid():N}@example.com";
        await apiFactory.Services.SeedPasswordUserAsync(
            new byte[32],
            authSalt: actualSalt,
            email: email,
            securityVersion: 1,
            userId: accountId);

        var (_, result) = await apiFactory.CreateClient()
            .POSTAsync<LoginSaltEndpoint, LoginSaltRequest, LoginSaltResponse>(new()
            {
                Email = email,
                ProfileId = "identity-argon2id-password-v1",
            });

        result.KdfSalt.ShouldBe(WebEncoders.Base64UrlEncode(actualSalt));
        result.AccountId.ShouldBe(accountId);
    }
}
