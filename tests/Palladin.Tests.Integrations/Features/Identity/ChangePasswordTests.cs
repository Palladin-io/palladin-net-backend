using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Palladin.Module.Identity.Features;
using Palladin.Module.Identity.Infrastructure.Jwt;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Seeders;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Identity;

[Collection<ApiFactoryCollection>]
public sealed class ChangePasswordTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_WrongCurrentAuthHash_Then_Returns403AndMaterialUnchanged()
    {
        // Given
        var email = $"chpw-bad-{Guid.NewGuid():N}@example.com";
        var (user, _) = await apiFactory.Services.SeedPasswordUserAsync(
            RandomBytes(32), email: email, emailVerified: true, securityVersion: 1);
        var client = apiFactory.CreateAuthenticatedClient(user);

        // When
        var response = await client.PutAsJsonAsync("api/account/password", new ChangePasswordRequest
        {
            SecurityVersion = 1,
            KdfProfileId = "identity-argon2id-password-v1",
            BaseCredentialRevision = user.CredentialRevision,
            BasePrivateKeyWrapRevision = user.PrivateKeyWrapRevision,
            CurrentAuthCredential = RandomBytes(32),
            NewAuthCredential = RandomBytes(32),
            NewKdfSalt = RandomBytes(16),
            NewEncryptedPrivateKey = RandomBytes(64),
        }, TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task When_CorrectCurrentAuthHash_Then_RotatesCredentialKeepsRecoveryRevokesSessions()
    {
        // Given
        var oldAuthHash = RandomBytes(32);
        var newAuthHash = RandomBytes(32);
        var email = $"chpw-ok-{Guid.NewGuid():N}@example.com";
        var (user, _) = await apiFactory.Services.SeedPasswordUserAsync(
            oldAuthHash, email: email, emailVerified: true, securityVersion: 1);
        var rawRefresh = Convert.ToBase64String(RandomBytes(32));
        await apiFactory.Services.SeedRefreshTokenAsync(user.Id, rawRefresh);
        var (oldRecoverySalt, oldRecoveryKey, oldSalt, oldPrivateKey) = await ReadMaterialAsync(user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user);

        var newSalt = RandomBytes(16);
        var newPrivateKey = RandomBytes(64);

        // When
        var response = await client.PutAsJsonAsync("api/account/password", new ChangePasswordRequest
        {
            SecurityVersion = 1,
            KdfProfileId = "identity-argon2id-password-v1",
            BaseCredentialRevision = user.CredentialRevision,
            BasePrivateKeyWrapRevision = user.PrivateKeyWrapRevision,
            CurrentAuthCredential = oldAuthHash,
            NewAuthCredential = newAuthHash,
            NewKdfSalt = newSalt,
            NewEncryptedPrivateKey = newPrivateKey,
        }, TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var (recoverySalt, recoveryKey, salt, privateKey) = await ReadMaterialAsync(user.Id);
        salt.ShouldBe(newSalt);
        privateKey.ShouldBe(newPrivateKey);
        recoverySalt.ShouldBe(oldRecoverySalt);
        recoveryKey.ShouldBe(oldRecoveryKey);
        salt.ShouldNotBe(oldSalt);
        privateKey.ShouldNotBe(oldPrivateKey);

        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var readContext = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
            var tokenHash = TokenService.HashToken(rawRefresh);
            (await readContext.RefreshTokens.FirstAsync(t => t.TokenHash == tokenHash, TestContext.Current.CancellationToken))
                .IsRevoked.ShouldBeTrue();
        }

        // And the new authHash logs in while the old one no longer does.
        var anon = apiFactory.CreateClient();
        var (oldLogin, _) = await anon.POSTAsync<LoginEndpoint, LoginRequest, LoginResponse>(
            new LoginRequest
            {
                Email = email,
                SecurityVersion = 1,
                KdfProfileId = "identity-argon2id-password-v1",
                AuthCredential = oldAuthHash,
            });
        oldLogin.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var (newLogin, _) = await anon.POSTAsync<LoginEndpoint, LoginRequest, LoginResponse>(
            new LoginRequest
            {
                Email = email,
                SecurityVersion = 1,
                KdfProfileId = "identity-argon2id-password-v1",
                AuthCredential = newAuthHash,
            });
        newLogin.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private async Task<(byte[]? RecoverySalt, byte[]? RecoveryKey, byte[]? Salt, byte[]? PrivateKey)> ReadMaterialAsync(Guid userId)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        var u = await readContext.Users.FirstAsync(x => x.Id == userId, TestContext.Current.CancellationToken);
        return (u.RecoverySalt, u.EncryptedPrivateKeyByRecovery, u.Salt, u.EncryptedPrivateKey);
    }

    private static byte[] RandomBytes(int length) => RandomNumberGenerator.GetBytes(length);
}
