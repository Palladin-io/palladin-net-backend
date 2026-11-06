using System.Net;
using System.Net.Http.Json;
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
            new LoginSaltRequest { Email = email });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.KdfSalt.ShouldBe(WebEncoders.Base64UrlEncode(authSalt));
        result.AccountId.ShouldNotBe(Guid.Empty);
        result.ProfileId.ShouldBe("identity-argon2id-password-v1");
        result.AccountSecretRequired.ShouldBeFalse();
    }

    [Fact]
    public async Task When_UnknownEmail_Then_ReturnsStablePseudoSaltIndistinguishableFromKnown()
    {
        // Given
        var email = $"ghost-{Guid.NewGuid():N}@example.com";
        var client = apiFactory.CreateClient();

        // When — the same unknown email twice
        var request = new LoginSaltRequest { Email = email };
        var (firstResponse, first) = await client.POSTAsync<LoginSaltEndpoint, LoginSaltRequest, LoginSaltResponse>(
            request);
        var (_, second) = await client.POSTAsync<LoginSaltEndpoint, LoginSaltRequest, LoginSaltResponse>(
            request);

        // Then — 200 like a known email, stable, and the same 16-byte length
        firstResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        first.KdfSalt.ShouldBe(second.KdfSalt);
        first.AccountId.ShouldBe(second.AccountId);
        first.AccountId.ShouldNotBe(Guid.Empty);
        first.AccountId.ToString("N")[12].ShouldBe('4');
        WebEncoders.Base64UrlDecode(first.KdfSalt).Length.ShouldBe(16);
        first.ProfileId.ShouldBe("identity-argon2id-account-secret-v2");
        first.SecurityVersion.ShouldBe((ushort)2);
        first.AccountSecretRequired.ShouldBeTrue();
    }

    [Fact]
    public async Task When_AccountUsesAccountSecretProfile_Then_ExactRegisteredParametersAreReturned()
    {
        // Given
        var kdfSalt = Enumerable.Range(0, 16).Select(index => (byte)(index + 20)).ToArray();
        var email = $"salt-v2-{Guid.NewGuid():N}@example.com";
        var (user, _) = await apiFactory.Services.SeedPasswordUserAsync(
            new byte[32],
            authSalt: kdfSalt,
            email: email,
            securityVersion: 2);

        // When
        var (response, result) = await apiFactory.CreateClient()
            .POSTAsync<LoginSaltEndpoint, LoginSaltRequest, LoginSaltResponse>(new() { Email = email });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.AccountId.ShouldBe(user.Id);
        result.ProfileId.ShouldBe("identity-argon2id-account-secret-v2");
        result.SecurityVersion.ShouldBe((ushort)2);
        result.KdfSalt.ShouldBe(WebEncoders.Base64UrlEncode(kdfSalt));
        result.MemoryKiB.ShouldBe(32768);
        result.Iterations.ShouldBe(2);
        result.Parallelism.ShouldBe(1);
        result.AccountSecretRequired.ShouldBeTrue();
    }

    [Fact]
    public async Task When_ClientAsksForOlderProfile_Then_AccountProfileStillWins()
    {
        // Given
        var email = $"salt-downgrade-{Guid.NewGuid():N}@example.com";
        await apiFactory.Services.SeedPasswordUserAsync(new byte[32], email: email, securityVersion: 2);

        // When
        var response = await apiFactory.CreateClient().PostAsJsonAsync(
            "api/auth/login/salt", new { email, profileId = "identity-argon2id-password-v1" });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<LoginSaltResponse>())!.ProfileId
            .ShouldBe("identity-argon2id-account-secret-v2");
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
            .POSTAsync<LoginSaltEndpoint, LoginSaltRequest, LoginSaltResponse>(new() { Email = email });

        result.KdfSalt.ShouldBe(WebEncoders.Base64UrlEncode(actualSalt));
        result.AccountId.ShouldBe(accountId);
    }
}
