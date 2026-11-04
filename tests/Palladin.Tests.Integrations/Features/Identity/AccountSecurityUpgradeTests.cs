using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using FastEndpoints;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Palladin.Core.Cryptography;
using Palladin.Module.Identity.Contracts.Commands;
using Palladin.Module.Identity.Features;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Vault.Contracts.Commands;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Identity;

[Collection<ApiFactoryCollection>]
public sealed class AccountSecurityUpgradeTests(ApiFactory apiFactory) : TestBase
{
    private const string LegacySuite = "palladin-x25519-sealed-box-v1";

    [Fact]
    public async Task When_V1AccountProvesCurrentCredential_Then_AllSecurityStateMovesToV2Atomically()
    {
        // Given
        var (user, client, authHash) = await SeedV1AccountAsync();
        var request = UpgradeRequest(authHash);

        // When
        var response = await client.PostAsJsonAsync("api/account/security-upgrade", request);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result = (await response.Content.ReadFromJsonAsync<UpgradeAccountSecurityResponse>())!;
        result.SecurityVersion.ShouldBe((ushort)2);
        result.MinimumSecurityVersion.ShouldBe((ushort)2);
        result.KdfProfileId.ShouldBe("identity-argon2id-account-secret-v2");
        result.MemberKeyVersion.ShouldBe(2u);
        result.MemberPublicKeySuiteId.ShouldBe(KeyWrapperSuites.Current.Id);
        result.LegacyMemberKeyVersion.ShouldBe(1u);

        var persisted = await ReadUserAsync(user.Id);
        persisted.Salt.ShouldBe(request.KdfSalt);
        persisted.PublicKey.ShouldBe(request.MemberPublicKey);
        persisted.EncryptedPrivateKey.ShouldBe(request.EncryptedPrivateKey);
        persisted.RecoverySalt.ShouldBe(request.RecoverySalt);
        persisted.EncryptedPrivateKeyByRecovery.ShouldBe(request.EncryptedPrivateKeyByRecovery);
        persisted.LegacyEncryptedPrivateKey.ShouldBe(request.LegacyEncryptedPrivateKey);
        persisted.CredentialRevision.ShouldBe(2u);
        persisted.PrivateKeyWrapRevision.ShouldBe(2u);
        (await LoginAsync(user.Email, request.NewAuthCredential, 2)).ShouldBe(HttpStatusCode.OK);
        (await LoginAsync(user.Email, authHash, 1)).ShouldBe(HttpStatusCode.Unauthorized);
        (await apiFactory.Services.GetRequiredService<ITestHarness>().Published.Any<UpsertMemberKeyDirectoryCommand>(
                x => x.Context.Message.UserId == user.Id && x.Context.Message.KeyVersion == 2
                     && x.Context.Message.PublicKeySuiteId == KeyWrapperSuites.Current.Id,
                TestContext.Current.CancellationToken))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task When_CommittedMigrationIsRetried_Then_ReturnsSameStateWithoutAnotherWrite()
    {
        // Given
        var (user, client, authHash) = await SeedV1AccountAsync();
        var request = UpgradeRequest(authHash);
        (await client.PostAsJsonAsync("api/account/security-upgrade", request)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // When
        var retry = await client.PostAsJsonAsync("api/account/security-upgrade", request with
        {
            CurrentAuthCredential = RandomNumberGenerator.GetBytes(32),
        });

        // Then
        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await retry.Content.ReadFromJsonAsync<UpgradeAccountSecurityResponse>())!.MemberKeyVersion.ShouldBe(2u);
        var persisted = await ReadUserAsync(user.Id);
        persisted.CredentialRevision.ShouldBe(2u);
        persisted.PublicKey.ShouldBe(request.MemberPublicKey);
    }

    [Fact]
    public async Task When_BaseRevisionIsStale_Then_ConflictLeavesV1StateUntouched()
    {
        // Given
        var (user, client, authHash) = await SeedV1AccountAsync();

        // When
        var response = await client.PostAsJsonAsync("api/account/security-upgrade",
            UpgradeRequest(authHash) with { BasePrivateKeyWrapRevision = 7 });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).ShouldContain("security-upgrade-conflict");
        await ShouldStillBeV1Async(user);
    }

    [Fact]
    public async Task When_ProofIsNotTheCurrentCredential_Then_ForbiddenWithoutChanges()
    {
        // Given
        var (user, client, _) = await SeedV1AccountAsync();

        // When
        var response = await client.PostAsJsonAsync("api/account/security-upgrade",
            UpgradeRequest(RandomNumberGenerator.GetBytes(32)));

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await ShouldStillBeV1Async(user);
    }

    [Fact]
    public async Task When_AccountIsAlreadyV2_Then_NewMigrationIsRejectedAsDowngradeConflict()
    {
        // Given
        var (_, client, authHash) = await SeedV1AccountAsync();
        var first = UpgradeRequest(authHash);
        (await client.PostAsJsonAsync("api/account/security-upgrade", first)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // When
        var response = await client.PostAsJsonAsync("api/account/security-upgrade",
            UpgradeRequest(first.NewAuthCredential) with { SourceSecurityVersion = 2, BaseMemberKeyVersion = 2 });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task When_NewMemberKeyUsesReadOnlySuite_Then_RequestFailsValidation()
    {
        // Given
        var (user, client, authHash) = await SeedV1AccountAsync();

        // When
        var response = await client.PostAsJsonAsync("api/account/security-upgrade", UpgradeRequest(authHash) with
        {
            MemberPublicKeySuiteId = LegacySuite,
            MemberPublicKey = RandomNumberGenerator.GetBytes(32),
        });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await ShouldStillBeV1Async(user);
    }

    [Fact]
    public async Task When_OAuthAccountHasNoStoredCredential_Then_UpgradeIssuesItsFirstAuthCredential()
    {
        // Given
        var (user, client, _) = await SeedV1AccountAsync(withPasswordCredential: false);
        var request = UpgradeRequest([]) with { BaseCredentialRevision = 0 };

        // When
        var response = await client.PostAsJsonAsync("api/account/security-upgrade", request);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var persisted = await ReadUserAsync(user.Id);
        persisted.CredentialRevision.ShouldBe(1u);
        persisted.SecurityVersion.ShouldBe((ushort)2);
        persisted.PublicKey.ShouldBe(request.MemberPublicKey);
        (await LoginAsync(user.Email, request.NewAuthCredential, 2)).ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task When_OAuthAccountSendsACredentialProof_Then_ConflictWithoutChanges()
    {
        // Given
        var (user, client, _) = await SeedV1AccountAsync(withPasswordCredential: false);

        // When
        var response = await client.PostAsJsonAsync("api/account/security-upgrade",
            UpgradeRequest(RandomNumberGenerator.GetBytes(32)) with { BaseCredentialRevision = 0 });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await ShouldStillBeV1Async(user);
    }

    [Fact]
    public async Task When_PasswordAccountOmitsTheCredentialProof_Then_ForbiddenWithoutChanges()
    {
        // Given
        var (user, client, _) = await SeedV1AccountAsync();

        // When
        var response = await client.PostAsJsonAsync("api/account/security-upgrade", UpgradeRequest([]));

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await ShouldStillBeV1Async(user);
    }

    [Fact]
    public async Task When_DeleteLegacyMemberKeyCommandIsConsumed_Then_OnlyTheMatchingVersionIsRemoved()
    {
        // Given
        var (user, client, authHash) = await SeedV1AccountAsync();
        (await client.PostAsJsonAsync("api/account/security-upgrade", UpgradeRequest(authHash)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // When
        await apiFactory.ConsumeAsync<DeleteLegacyMemberKeyConsumer, DeleteLegacyMemberKeyCommand>(
            new DeleteLegacyMemberKeyCommand(user.Id, 2));
        var afterMismatch = await ReadUserAsync(user.Id);
        await apiFactory.ConsumeAsync<DeleteLegacyMemberKeyConsumer, DeleteLegacyMemberKeyCommand>(
            new DeleteLegacyMemberKeyCommand(user.Id, 1));

        // Then
        afterMismatch.LegacyMemberKeyVersion.ShouldBe(1u);
        var persisted = await ReadUserAsync(user.Id);
        persisted.LegacyEncryptedPrivateKey.ShouldBeNull();
        persisted.LegacyMemberKeyVersion.ShouldBeNull();
        var (_, account) = await client.GETAsync<GetAccountEndpoint, GetAccountResponse>();
        account.LegacyMemberKey.ShouldBeNull();
        account.MemberPublicKeySuiteId.ShouldBe(KeyWrapperSuites.Current.Id);
    }

    private async Task<(Palladin.Module.Identity.Domain.User User, HttpClient Client, byte[] AuthHash)> SeedV1AccountAsync(
        bool withPasswordCredential = true)
    {
        var authHash = RandomNumberGenerator.GetBytes(32);
        var (user, _) = await apiFactory.Services.SeedPasswordUserAsync(
            authHash, email: $"upgrade-{Guid.NewGuid():N}@example.com", emailVerified: true, securityVersion: 1,
            withPasswordCredential: withPasswordCredential);
        await apiFactory.Services.SeedMemberKeyDirectoryAsync(user.Id, RandomNumberGenerator.GetBytes(32), 1, LegacySuite);
        return (user, apiFactory.CreateAuthenticatedClient(user), authHash);
    }

    private static UpgradeAccountSecurityRequest UpgradeRequest(byte[] currentAuthCredential) => new()
    {
        MigrationId = Guid.NewGuid(),
        SourceSecurityVersion = 1,
        BaseCredentialRevision = 1,
        BasePrivateKeyWrapRevision = 1,
        BaseMemberKeyVersion = 1,
        SecurityVersion = 2,
        KdfProfileId = "identity-argon2id-account-secret-v2",
        CurrentAuthCredential = currentAuthCredential,
        NewAuthCredential = RandomNumberGenerator.GetBytes(32),
        KdfSalt = RandomNumberGenerator.GetBytes(16),
        MemberPublicKeySuiteId = KeyWrapperSuites.Current.Id,
        MemberPublicKey = RandomNumberGenerator.GetBytes(KeyWrapperSuites.Current.PublicKeyBytes),
        EncryptedPrivateKey = RandomNumberGenerator.GetBytes(72),
        RecoverySalt = RandomNumberGenerator.GetBytes(16),
        EncryptedPrivateKeyByRecovery = RandomNumberGenerator.GetBytes(72),
        LegacyEncryptedPrivateKey = RandomNumberGenerator.GetBytes(72),
    };

    private async Task<HttpStatusCode> LoginAsync(string email, byte[] authCredential, ushort securityVersion)
    {
        var response = await apiFactory.CreateClient().PostAsJsonAsync("api/auth/login", new LoginRequest
        {
            Email = email,
            SecurityVersion = securityVersion,
            KdfProfileId = securityVersion == 2 ? "identity-argon2id-account-secret-v2" : "identity-argon2id-password-v1",
            AuthCredential = authCredential,
        });
        return response.StatusCode;
    }

    private async Task ShouldStillBeV1Async(Palladin.Module.Identity.Domain.User user)
    {
        var persisted = await ReadUserAsync(user.Id);
        persisted.SecurityVersion.ShouldBe((ushort)1);
        persisted.KdfProfileId.ShouldBe("identity-argon2id-password-v1");
        persisted.MemberKeyVersion.ShouldBe(1u);
        persisted.MemberPublicKeySuiteId.ShouldBe(LegacySuite);
        persisted.CredentialRevision.ShouldBe(user.CredentialRevision);
        persisted.LegacyEncryptedPrivateKey.ShouldBeNull();
    }

    private async Task<Palladin.Module.Identity.Domain.User> ReadUserAsync(Guid userId)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>().Users
            .SingleAsync(x => x.Id == userId, TestContext.Current.CancellationToken);
    }
}
