using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Palladin.Module.Identity.Features;
using Palladin.Module.Identity.Infrastructure.Jwt;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Fakers;
using Palladin.Tests.Integrations.Shared.Seeders;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Identity;

[Collection<ApiFactoryCollection>]
public sealed class RecoverAccountTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_AuthenticatedUser_IsOnboarded_Then_RecoveryUpdatesKeys()
    {
        // Given
        var userFaker = UserFaker.CreateOnboarded()
            .RuleFor(x => x.PublicKey, Enumerable.Range(0, 32).Select(i => (byte)i).ToArray())
            .RuleFor(x => x.Salt, Enumerable.Range(0, 16).Select(i => (byte)i).ToArray())
            .RuleFor(x => x.RecoverySalt, Enumerable.Range(0, 16).Select(i => (byte)(i + 0x40)).ToArray())
            .RuleFor(x => x.EncryptedPrivateKey, Enumerable.Range(0, 32).Select(i => (byte)(i + 0x20)).ToArray())
            .RuleFor(x => x.EncryptedPrivateKeyByRecovery, Enumerable.Range(0, 32).Select(i => (byte)(i + 0x30)).ToArray())
            .RuleFor(x => x.DeviceWrapperMetadata, Enumerable.Repeat((byte)0xD0, 32).ToArray());
        var (user, _, _) = await apiFactory.Services.SeedUserAsync(userFaker);
        var client = apiFactory.CreateAuthenticatedClient(user);

        var newSalt = Enumerable.Range(0, 16).Select(i => (byte)(i + 0x80)).ToArray();
        var newEncryptedPrivateKey = Enumerable.Range(0, 64).Select(i => (byte)(i + 0x90)).ToArray();
        var newRecoverySalt = Enumerable.Range(0, 16).Select(i => (byte)(i + 0xA0)).ToArray();
        var newEncryptedPrivateKeyByRecovery = Enumerable.Range(0, 64).Select(i => (byte)(i + 0xB0)).ToArray();

        var payload = new RecoverAccountRequest
        {
            SecurityVersion = 1,
            KdfProfileId = "identity-argon2id-password-v1",
            BaseCredentialRevision = user.CredentialRevision,
            BasePrivateKeyWrapRevision = user.PrivateKeyWrapRevision,
            NewKdfSalt = newSalt,
            NewEncryptedPrivateKey = newEncryptedPrivateKey,
            NewRecoverySalt = newRecoverySalt,
            NewEncryptedPrivateKeyByRecovery = newEncryptedPrivateKeyByRecovery,
        };

        // When
        var response = await client.PutAsJsonAsync("api/account/recovery", payload);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        var persisted = await readContext.Users.FirstAsync(u => u.Id == user.Id);
        persisted.Salt.ShouldBe(newSalt);
        persisted.EncryptedPrivateKey.ShouldBe(newEncryptedPrivateKey);
        persisted.RecoverySalt.ShouldBe(newRecoverySalt);
        persisted.EncryptedPrivateKeyByRecovery.ShouldBe(newEncryptedPrivateKeyByRecovery);
        persisted.SecurityVersion.ShouldBe((ushort)1);
        persisted.MinimumSecurityVersion.ShouldBe((ushort)1);
        persisted.KdfProfileId.ShouldBe("identity-argon2id-password-v1");
        persisted.CredentialRevision.ShouldBe(0u);
        persisted.PrivateKeyWrapRevision.ShouldBe(2u);
        persisted.DeviceWrapperMetadata.ShouldBeNull();
    }

    [Fact]
    public async Task When_AuthenticatedUser_NotOnboarded_Then_Returns404()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user);

        var payload = new RecoverAccountRequest
        {
            SecurityVersion = 1,
            KdfProfileId = "identity-argon2id-password-v1",
            BaseCredentialRevision = user.CredentialRevision,
            BasePrivateKeyWrapRevision = 1,
            NewKdfSalt = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray(),
            NewEncryptedPrivateKey = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray(),
            NewRecoverySalt = Enumerable.Range(0, 16).Select(i => (byte)(i + 0x40)).ToArray(),
            NewEncryptedPrivateKeyByRecovery = Enumerable.Range(0, 64).Select(i => (byte)(i + 0x50)).ToArray(),
        };

        // When
        var response = await client.PutAsJsonAsync("api/account/recovery", payload);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task When_Unauthenticated_Then_Returns401()
    {
        // Given
        var client = apiFactory.CreateClient();

        var payload = new RecoverAccountRequest
        {
            SecurityVersion = 1,
            KdfProfileId = "identity-argon2id-password-v1",
            BaseCredentialRevision = 0u,
            BasePrivateKeyWrapRevision = 1u,
            NewKdfSalt = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray(),
            NewEncryptedPrivateKey = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray(),
            NewRecoverySalt = Enumerable.Range(0, 16).Select(i => (byte)(i + 0x40)).ToArray(),
            NewEncryptedPrivateKeyByRecovery = Enumerable.Range(0, 64).Select(i => (byte)(i + 0x50)).ToArray(),
        };

        // When
        var response = await client.PutAsJsonAsync("api/account/recovery", payload);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task When_AuthenticatedUser_IsOnboarded_Then_RecoveryRevokesActiveRefreshTokens()
    {
        // Given
        var userFaker = UserFaker.CreateOnboarded()
            .RuleFor(x => x.PublicKey, Enumerable.Range(0, 32).Select(i => (byte)i).ToArray())
            .RuleFor(x => x.Salt, Enumerable.Range(0, 16).Select(i => (byte)i).ToArray())
            .RuleFor(x => x.RecoverySalt, Enumerable.Range(0, 16).Select(i => (byte)(i + 0x40)).ToArray())
            .RuleFor(x => x.EncryptedPrivateKey, Enumerable.Range(0, 32).Select(i => (byte)(i + 0x20)).ToArray())
            .RuleFor(x => x.EncryptedPrivateKeyByRecovery, Enumerable.Range(0, 32).Select(i => (byte)(i + 0x30)).ToArray());
        var (user, _, _) = await apiFactory.Services.SeedUserAsync(userFaker);
        var rawToken = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)(i + 0x50)).ToArray());
        await apiFactory.Services.SeedRefreshTokenAsync(user.Id, rawToken);
        var client = apiFactory.CreateAuthenticatedClient(user);

        var payload = new RecoverAccountRequest
        {
            SecurityVersion = 1,
            KdfProfileId = "identity-argon2id-password-v1",
            BaseCredentialRevision = user.CredentialRevision,
            BasePrivateKeyWrapRevision = user.PrivateKeyWrapRevision,
            NewKdfSalt = Enumerable.Range(0, 16).Select(i => (byte)(i + 0x80)).ToArray(),
            NewEncryptedPrivateKey = Enumerable.Range(0, 64).Select(i => (byte)(i + 0x90)).ToArray(),
            NewRecoverySalt = Enumerable.Range(0, 16).Select(i => (byte)(i + 0xA0)).ToArray(),
            NewEncryptedPrivateKeyByRecovery = Enumerable.Range(0, 64).Select(i => (byte)(i + 0xB0)).ToArray(),
        };

        // When
        var response = await client.PutAsJsonAsync("api/account/recovery", payload);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        var tokenHash = TokenService.HashToken(rawToken);
        var revokedToken = await readContext.RefreshTokens.FirstOrDefaultAsync(rt => rt.TokenHash == tokenHash);
        revokedToken.ShouldNotBeNull();
        revokedToken.IsRevoked.ShouldBeTrue();
    }

    [Fact]
    public async Task When_PasswordUser_RecoversWithNewAuthHash_Then_OldRejectedNewAccepted()
    {
        // Given
        var oldAuthHash = RandomNumberGenerator.GetBytes(32);
        var newAuthHash = RandomNumberGenerator.GetBytes(32);
        var email = $"recover-pwd-{Guid.NewGuid():N}@example.com";
        var (user, _) = await apiFactory.Services.SeedPasswordUserAsync(oldAuthHash, email: email, emailVerified: true);
        var client = apiFactory.CreateAuthenticatedClient(user);

        var payload = new RecoverAccountRequest
        {
            SecurityVersion = 1,
            KdfProfileId = "identity-argon2id-password-v1",
            BaseCredentialRevision = user.CredentialRevision,
            BasePrivateKeyWrapRevision = user.PrivateKeyWrapRevision,
            NewKdfSalt = Enumerable.Range(0, 16).Select(i => (byte)(i + 0x80)).ToArray(),
            NewEncryptedPrivateKey = Enumerable.Range(0, 64).Select(i => (byte)(i + 0x90)).ToArray(),
            NewRecoverySalt = Enumerable.Range(0, 16).Select(i => (byte)(i + 0xA0)).ToArray(),
            NewEncryptedPrivateKeyByRecovery = Enumerable.Range(0, 64).Select(i => (byte)(i + 0xB0)).ToArray(),
            NewAuthCredential = newAuthHash,
        };

        // When
        var recoverResponse = await client.PutAsJsonAsync("api/account/recovery", payload, TestContext.Current.CancellationToken);

        // Then
        recoverResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var anonClient = apiFactory.CreateClient();
        var (oldLogin, _) = await anonClient.POSTAsync<LoginEndpoint, LoginRequest, LoginResponse>(
            new LoginRequest { Email = email, SecurityVersion = 1, KdfProfileId = "identity-argon2id-password-v1", AuthCredential = oldAuthHash });
        oldLogin.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var (newLogin, newResult) = await anonClient.POSTAsync<LoginEndpoint, LoginRequest, LoginResponse>(
            new LoginRequest
            {
                Email = email,
                SecurityVersion = 1,
                KdfProfileId = "identity-argon2id-password-v1",
                AuthCredential = newAuthHash,
            });
        newLogin.StatusCode.ShouldBe(HttpStatusCode.OK);
        newResult.AccessToken.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task When_InvalidBase64_Then_Returns400()
    {
        // Given
        var userFaker = UserFaker.CreateOnboarded()
            .RuleFor(x => x.PublicKey, Enumerable.Range(0, 32).Select(i => (byte)i).ToArray())
            .RuleFor(x => x.Salt, Enumerable.Range(0, 16).Select(i => (byte)i).ToArray())
            .RuleFor(x => x.RecoverySalt, Enumerable.Range(0, 16).Select(i => (byte)(i + 0x40)).ToArray())
            .RuleFor(x => x.EncryptedPrivateKey, Enumerable.Range(0, 32).Select(i => (byte)(i + 0x20)).ToArray())
            .RuleFor(x => x.EncryptedPrivateKeyByRecovery, Enumerable.Range(0, 32).Select(i => (byte)(i + 0x30)).ToArray());
        var (user, _, _) = await apiFactory.Services.SeedUserAsync(userFaker);
        var client = apiFactory.CreateAuthenticatedClient(user);

        // Send raw JSON with an invalid base64 string — System.Text.Json rejects it
        // before validation and returns 400.
        var json = """{"SecurityVersion":1,"KdfProfileId":"identity-argon2id-password-v1","BasePrivateKeyWrapRevision":1,"NewKdfSalt":"not-valid-base64!!!","NewEncryptedPrivateKey":"AAECBAUGB","NewRecoverySalt":"AAECBAUG","NewEncryptedPrivateKeyByRecovery":"AAECBAUGB"}""";
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        // When
        var response = await client.PutAsync("api/account/recovery", content);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task When_RecoveryUsesStaleWrapperRevision_Then_NoIdentityMaterialChanges()
    {
        var userFaker = UserFaker.CreateOnboarded()
            .RuleFor(x => x.PublicKey, RandomNumberGenerator.GetBytes(32))
            .RuleFor(x => x.Salt, RandomNumberGenerator.GetBytes(16))
            .RuleFor(x => x.RecoverySalt, RandomNumberGenerator.GetBytes(16))
            .RuleFor(x => x.EncryptedPrivateKey, RandomNumberGenerator.GetBytes(64))
            .RuleFor(x => x.EncryptedPrivateKeyByRecovery, RandomNumberGenerator.GetBytes(64));
        var (user, _, _) = await apiFactory.Services.SeedUserAsync(userFaker);
        var originalSalt = user.Salt!.ToArray();
        var originalPrivateKey = user.EncryptedPrivateKey!.ToArray();

        var response = await apiFactory.CreateAuthenticatedClient(user).PutAsJsonAsync(
            "api/account/recovery",
            new RecoverAccountRequest
            {
                SecurityVersion = 1,
                KdfProfileId = "identity-argon2id-password-v1",
                BaseCredentialRevision = 0u,
                BasePrivateKeyWrapRevision = user.PrivateKeyWrapRevision + 1,
                NewKdfSalt = RandomNumberGenerator.GetBytes(16),
                NewEncryptedPrivateKey = RandomNumberGenerator.GetBytes(64),
                NewRecoverySalt = RandomNumberGenerator.GetBytes(16),
                NewEncryptedPrivateKeyByRecovery = RandomNumberGenerator.GetBytes(64),
            },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var persisted = await scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>()
            .Users.SingleAsync(account => account.Id == user.Id, TestContext.Current.CancellationToken);
        persisted.SecurityVersion.ShouldBe((ushort)1);
        persisted.Salt.ShouldBe(originalSalt);
        persisted.EncryptedPrivateKey.ShouldBe(originalPrivateKey);
        persisted.PrivateKeyWrapRevision.ShouldBe(1u);
    }
}
