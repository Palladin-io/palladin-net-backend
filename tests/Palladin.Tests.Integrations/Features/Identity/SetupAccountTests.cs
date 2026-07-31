using System.Net;
using System.Net.Http.Json;
using FastEndpoints;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Features;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Fakers;
using Palladin.Tests.Integrations.Shared.Seeders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Identity;

[Collection<ApiFactoryCollection>]
public sealed class SetupAccountTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_AuthenticatedUser_WithoutKeys_SetsUpAccount_Then_StoresKeyMaterial()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user);

        var payload = new SetupAccountRequest
        {
            SecurityVersion = 1,
            KdfProfileId = "identity-argon2id-password-v1",
            KdfSalt = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray(),
            RecoverySalt = Enumerable.Range(0, 16).Select(i => (byte)(i + 0x40)).ToArray(),
            PublicKey = Enumerable.Range(0, 32).Select(i => (byte)(i + 0x10)).ToArray(),
            EncryptedPrivateKey = Enumerable.Range(0, 32).Select(i => (byte)(i + 0x20)).ToArray(),
            EncryptedPrivateKeyByRecovery = Enumerable.Range(0, 32).Select(i => (byte)(i + 0x30)).ToArray(),
            NewAuthCredential = RandomNumberGenerator.GetBytes(32),
        };

        // When
        var response = await client.PostAsJsonAsync("api/account/setup", payload);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        var persisted = await readContext.Users.FirstAsync(u => u.Id == user.Id);
        persisted.Salt.ShouldBe(payload.KdfSalt);
        persisted.RecoverySalt.ShouldBe(payload.RecoverySalt);
        persisted.PublicKey.ShouldBe(payload.PublicKey);
        persisted.MemberKeyVersion.ShouldBe((uint)1);
        persisted.EncryptedPrivateKey.ShouldBe(payload.EncryptedPrivateKey);
        persisted.EncryptedPrivateKeyByRecovery.ShouldBe(payload.EncryptedPrivateKeyByRecovery);
        persisted.CredentialRevision.ShouldBe((uint)1);
        var credential = await readContext.PasswordCredentials.SingleAsync(x => x.UserId == user.Id);
        credential.AuthSalt.ShouldBe(payload.KdfSalt);

        var anonymousClient = apiFactory.CreateClient();
        var (loginResponse, login) = await anonymousClient.POSTAsync<LoginEndpoint, LoginRequest, LoginResponse>(
            new LoginRequest
            {
                Email = user.Email,
                SecurityVersion = 1,
                KdfProfileId = "identity-argon2id-password-v1",
                AuthCredential = payload.NewAuthCredential,
            });
        loginResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        login.AccessToken.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task When_AuthenticatedUser_AlreadySetupKeys_Then_Returns409()
    {
        // Given
        var userFaker = UserFaker.CreateOnboarded()
            .RuleFor(x => x.PublicKey, [0xAA, 0xBB])
            .RuleFor(x => x.Salt, [0xCC])
            .RuleFor(x => x.RecoverySalt, [0xCD])
            .RuleFor(x => x.EncryptedPrivateKey, [0xDD])
            .RuleFor(x => x.EncryptedPrivateKeyByRecovery, [0xEE]);
        var (user, _, _) = await apiFactory.Services.SeedUserAsync(userFaker);
        var client = apiFactory.CreateAuthenticatedClient(user);

        var payload = new SetupAccountRequest
        {
            SecurityVersion = 1,
            KdfProfileId = "identity-argon2id-password-v1",
            KdfSalt = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray(),
            RecoverySalt = Enumerable.Range(0, 16).Select(i => (byte)(i + 0x40)).ToArray(),
            PublicKey = Enumerable.Range(0, 32).Select(i => (byte)(i + 0x10)).ToArray(),
            EncryptedPrivateKey = Enumerable.Range(0, 32).Select(i => (byte)(i + 0x20)).ToArray(),
            EncryptedPrivateKeyByRecovery = Enumerable.Range(0, 32).Select(i => (byte)(i + 0x30)).ToArray(),
            NewAuthCredential = RandomNumberGenerator.GetBytes(32),
        };

        // When
        var response = await client.PostAsJsonAsync("api/account/setup", payload);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task When_ExactSetupRetryArrives_Then_DoesNotReplaceStoredKeyMaterial()
    {
        var storedPublicKey = Enumerable.Range(0, 32).Select(i => (byte)(i + 0x10)).ToArray();
        var storedSalt = Enumerable.Repeat((byte)0xA1, 16).ToArray();
        var storedRecoverySalt = Enumerable.Repeat((byte)0xA2, 16).ToArray();
        var storedEncryptedPrivateKey = Enumerable.Repeat((byte)0xA3, 32).ToArray();
        var storedEncryptedPrivateKeyByRecovery = Enumerable.Repeat((byte)0xA4, 32).ToArray();
        var userFaker = UserFaker.CreateOnboarded()
            .RuleFor(x => x.PublicKey, storedPublicKey)
            .RuleFor(x => x.MemberKeyVersion, (uint)1)
            .RuleFor(x => x.Salt, storedSalt)
            .RuleFor(x => x.RecoverySalt, storedRecoverySalt)
            .RuleFor(x => x.EncryptedPrivateKey, storedEncryptedPrivateKey)
            .RuleFor(x => x.EncryptedPrivateKeyByRecovery, storedEncryptedPrivateKeyByRecovery);
        var (user, _, _) = await apiFactory.Services.SeedUserAsync(userFaker);
        var client = apiFactory.CreateAuthenticatedClient(user);
        var payload = new SetupAccountRequest
        {
            SecurityVersion = 1,
            KdfProfileId = "identity-argon2id-password-v1",
            KdfSalt = Enumerable.Repeat((byte)0xB1, 16).ToArray(),
            RecoverySalt = Enumerable.Repeat((byte)0xB2, 16).ToArray(),
            PublicKey = storedPublicKey,
            EncryptedPrivateKey = Enumerable.Repeat((byte)0xB3, 32).ToArray(),
            EncryptedPrivateKeyByRecovery = Enumerable.Repeat((byte)0xB4, 32).ToArray(),
            NewAuthCredential = RandomNumberGenerator.GetBytes(32),
        };

        var response = await client.PostAsJsonAsync("api/account/setup", payload);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var persisted = await scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>()
            .Users.SingleAsync(x => x.Id == user.Id);
        persisted.Salt.ShouldBe(storedSalt);
        persisted.RecoverySalt.ShouldBe(storedRecoverySalt);
        persisted.PublicKey.ShouldBe(storedPublicKey);
        persisted.MemberKeyVersion.ShouldBe((uint)1);
        persisted.EncryptedPrivateKey.ShouldBe(storedEncryptedPrivateKey);
        persisted.EncryptedPrivateKeyByRecovery.ShouldBe(storedEncryptedPrivateKeyByRecovery);
        persisted.CredentialRevision.ShouldBe((uint)1);
        var credential = await scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>()
            .PasswordCredentials.SingleAsync(x => x.UserId == user.Id);
        credential.AuthSalt.ShouldBe(storedSalt);

        var (loginResponse, login) = await apiFactory.CreateClient()
            .POSTAsync<LoginEndpoint, LoginRequest, LoginResponse>(new LoginRequest
            {
                Email = user.Email,
                SecurityVersion = 1,
                KdfProfileId = "identity-argon2id-password-v1",
                AuthCredential = payload.NewAuthCredential,
            });
        loginResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        login.AccessToken.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task When_Unauthenticated_Then_Returns401()
    {
        // Given
        var client = apiFactory.CreateClient();

        var payload = new SetupAccountRequest
        {
            SecurityVersion = 1,
            KdfProfileId = "identity-argon2id-password-v1",
            KdfSalt = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray(),
            RecoverySalt = Enumerable.Range(0, 16).Select(i => (byte)(i + 0x40)).ToArray(),
            PublicKey = Enumerable.Range(0, 32).Select(i => (byte)(i + 0x10)).ToArray(),
            EncryptedPrivateKey = Enumerable.Range(0, 32).Select(i => (byte)(i + 0x20)).ToArray(),
            EncryptedPrivateKeyByRecovery = Enumerable.Range(0, 32).Select(i => (byte)(i + 0x30)).ToArray(),
            NewAuthCredential = RandomNumberGenerator.GetBytes(32),
        };

        // When
        var response = await client.PostAsJsonAsync("api/account/setup", payload);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
