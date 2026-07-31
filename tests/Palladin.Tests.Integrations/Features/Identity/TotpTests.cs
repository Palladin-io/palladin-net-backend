using System.Net;
using System.Net.Http.Json;
using Palladin.Module.Identity.Features;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Shared;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Seeders;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using OtpNet;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Identity;

[Collection<ApiFactoryCollection>]
public sealed class TotpTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_EnrollConfirmThenLoginWithFreshCode_Then_ChallengeCompletes()
    {
        // Given
        apiFactory.GuidProvider.Generate().Returns(_ => Guid.NewGuid());
        var authHash = KeyGeneration.GenerateRandomKey(32);
        var email = $"totp-code-{Guid.NewGuid():N}@example.com";
        var (user, _) = await apiFactory.Services.SeedPasswordUserAsync(authHash, email: email, emailVerified: true);
        var authedClient = apiFactory.CreateAuthenticatedClient(user);

        var enroll = await EnrollAsync(authedClient);
        var (confirmResponse, _) = await authedClient.POSTAsync<ConfirmTotpEndpoint, ConfirmTotpRequest, ConfirmTotpResponse>(
            new ConfirmTotpRequest { Code = Code(enroll.Secret) });
        confirmResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var anonClient = apiFactory.CreateClient();
        var (_, login) = await anonClient.POSTAsync<LoginEndpoint, LoginRequest, LoginResponse>(
            new LoginRequest { Email = email, SecurityVersion = 1, KdfProfileId = "identity-argon2id-password-v1", AuthCredential = authHash });
        login.TotpRequired.ShouldBeTrue();

        // When — a fresh (next-step) code, since the confirm code's step is now consumed
        var (response, result) = await anonClient.POSTAsync<LoginTotpEndpoint, LoginTotpRequest, AuthSessionResponse>(
            new LoginTotpRequest { ChallengeToken = login.ChallengeToken!, Code = NextCode(enroll.Secret) });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.AccessToken.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task When_ConfirmCodeReplayedOnLoginTotp_Then_Rejected()
    {
        // Given — the exact code used at confirm must not complete a login within the same window.
        apiFactory.GuidProvider.Generate().Returns(_ => Guid.NewGuid());
        var authHash = KeyGeneration.GenerateRandomKey(32);
        var email = $"totp-replay-{Guid.NewGuid():N}@example.com";
        var (user, _) = await apiFactory.Services.SeedPasswordUserAsync(authHash, email: email, emailVerified: true);
        var authedClient = apiFactory.CreateAuthenticatedClient(user);
        var enroll = await EnrollAsync(authedClient);
        var confirmCode = Code(enroll.Secret);
        await authedClient.POSTAsync<ConfirmTotpEndpoint, ConfirmTotpRequest, ConfirmTotpResponse>(
            new ConfirmTotpRequest { Code = confirmCode });

        var anonClient = apiFactory.CreateClient();
        var (_, login) = await anonClient.POSTAsync<LoginEndpoint, LoginRequest, LoginResponse>(
            new LoginRequest { Email = email, SecurityVersion = 1, KdfProfileId = "identity-argon2id-password-v1", AuthCredential = authHash });

        // When — replay the confirm code
        var (response, _) = await anonClient.POSTAsync<LoginTotpEndpoint, LoginTotpRequest, AuthSessionResponse>(
            new LoginTotpRequest { ChallengeToken = login.ChallengeToken!, Code = confirmCode });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task When_LoginTotpWithRecoveryCode_Then_IssuesSession()
    {
        // Given
        apiFactory.GuidProvider.Generate().Returns(_ => Guid.NewGuid());
        var authHash = KeyGeneration.GenerateRandomKey(32);
        var email = $"totp-rec-{Guid.NewGuid():N}@example.com";
        var (user, _) = await apiFactory.Services.SeedPasswordUserAsync(authHash, email: email, emailVerified: true);
        var authedClient = apiFactory.CreateAuthenticatedClient(user);
        var enroll = await EnrollAsync(authedClient);
        var (_, confirm) = await authedClient.POSTAsync<ConfirmTotpEndpoint, ConfirmTotpRequest, ConfirmTotpResponse>(
            new ConfirmTotpRequest { Code = Code(enroll.Secret) });

        var anonClient = apiFactory.CreateClient();
        var (_, login) = await anonClient.POSTAsync<LoginEndpoint, LoginRequest, LoginResponse>(
            new LoginRequest { Email = email, SecurityVersion = 1, KdfProfileId = "identity-argon2id-password-v1", AuthCredential = authHash });

        // When
        var (response, result) = await anonClient.POSTAsync<LoginTotpEndpoint, LoginTotpRequest, AuthSessionResponse>(
            new LoginTotpRequest { ChallengeToken = login.ChallengeToken!, Code = confirm.RecoveryCodes[0] });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.AccessToken.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task When_DisableWithValidCode_Then_FactorRemoved()
    {
        // Given
        apiFactory.GuidProvider.Generate().Returns(_ => Guid.NewGuid());
        var email = $"totp-off-{Guid.NewGuid():N}@example.com";
        var (user, _) = await apiFactory.Services.SeedPasswordUserAsync(KeyGeneration.GenerateRandomKey(32), email: email, emailVerified: true);
        var authedClient = apiFactory.CreateAuthenticatedClient(user);
        var enroll = await EnrollAsync(authedClient);
        await authedClient.POSTAsync<ConfirmTotpEndpoint, ConfirmTotpRequest, ConfirmTotpResponse>(
            new ConfirmTotpRequest { Code = Code(enroll.Secret) });

        // When — a fresh (next-step) code, since the confirm code's step is consumed
        var response = await authedClient.PostAsJsonAsync(
            "api/auth/totp/disable", new { Code = NextCode(enroll.Secret) }, TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        (await readContext.TotpCredentials.FirstAsync(t => t.UserId == user.Id, TestContext.Current.CancellationToken))
            .IsEnabled.ShouldBeFalse();
    }

    private async Task<EnrollTotpResponse> EnrollAsync(HttpClient client)
    {
        var response = await client.PostAsync("api/auth/totp/enroll", null, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<EnrollTotpResponse>(TestContext.Current.CancellationToken))!;
    }

    private static string Code(string secretBase32) =>
        new Totp(Base32Encoding.ToBytes(secretBase32)).ComputeTotp();

    // Code for the next 30s step — accepted within the ±1 window but a different, unconsumed step.
    private static string NextCode(string secretBase32) =>
        new Totp(Base32Encoding.ToBytes(secretBase32)).ComputeTotp(DateTime.UtcNow.AddSeconds(30));
}
