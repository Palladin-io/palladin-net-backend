using System.IdentityModel.Tokens.Jwt;
using System.Net;
using Palladin.Core.Security;
using Palladin.Module.Identity.Contracts.ValueObjects;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Domain.Enums;
using Palladin.Module.Identity.Features;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Shared;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Seeders;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Identity;

[Collection<ApiFactoryCollection>]
public sealed class RegisterTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_RegisteringNewEmail_Then_StoresMaterialUnverifiedAndIssuesSession()
    {
        // Given
        apiFactory.GuidProvider.Generate().Returns(_ => Guid.NewGuid());
        var email = $"reg-{Guid.NewGuid():N}@example.com";
        var accountId = Guid.NewGuid();
        var client = apiFactory.CreateClient();

        // When
        var (response, result) = await client.POSTAsync<RegisterEndpoint, RegisterRequest, AuthSessionResponse>(
            NewRequest(email, accountId: accountId));

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.EmailVerified.ShouldBeFalse();
        result.IsOnboarded.ShouldBeTrue();
        result.AccessToken.ShouldNotBeNullOrWhiteSpace();
        result.RefreshToken.ShouldNotBeNullOrWhiteSpace();
        result.UserId.ShouldBe(accountId);
        var accessTokenClaims = new JwtSecurityTokenHandler().ReadJwtToken(result.AccessToken).Claims;
        accessTokenClaims.Single(claim => claim.Type == JwtClaimNames.OrganizationOfflineAccessPolicy)
            .Value.ShouldBe(((ushort)OrganizationOfflineAccessPolicy.TwentyFourHours).ToString());
        accessTokenClaims.Single(claim => claim.Type == JwtClaimNames.OrganizationOfflineAccessPolicyVersion)
            .Value.ShouldBe("1");

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        var user = await readContext.Users.FirstAsync(u => u.Email == email, TestContext.Current.CancellationToken);
        user.Id.ShouldBe(accountId);
        user.EmailVerified.ShouldBeFalse();
        user.IsOnboarded.ShouldBeTrue();
        user.MemberKeyVersion.ShouldBe((uint)1);

        var roles = await readContext.Roles
            .Where(role => role.OrganizationId == user.OrganizationId)
            .OrderBy(role => role.Name)
            .ToListAsync(TestContext.Current.CancellationToken);
        roles.Select(role => role.Name).ShouldBe([Role.AdministratorName, Role.DefaultUserName]);
        var defaultUserRole = roles.Single(role => role.IsDefaultUser);
        defaultUserRole.IsSystem.ShouldBeTrue();
        defaultUserRole.Permissions.ShouldBe(Role.DefaultUserPermissions);

        var owner = await readContext.OrganizationMembers
            .Include(member => member.RoleAssignments)
                .ThenInclude(assignment => assignment.Role)
            .SingleAsync(
                member => member.OrganizationId == user.OrganizationId && member.UserId == user.Id,
                TestContext.Current.CancellationToken);
        owner.RoleAssignments.ShouldHaveSingleItem().Role.IsAdministrator.ShouldBeTrue();

        (await readContext.PasswordCredentials.AnyAsync(c => c.UserId == user.Id, TestContext.Current.CancellationToken))
            .ShouldBeTrue();
        (await readContext.VerificationTokens.AnyAsync(
            t => t.UserId == user.Id && t.Purpose == VerificationTokenPurpose.EmailVerify,
            TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    [Fact]
    public async Task When_AccountIdIsEmpty_Then_RejectsRequestWithoutCreatingAccount()
    {
        // Given
        var email = $"empty-id-{Guid.NewGuid():N}@example.com";
        var client = apiFactory.CreateClient();

        // When
        var (response, _) = await client.POSTAsync<RegisterEndpoint, RegisterRequest, AuthSessionResponse>(
            NewRequest(email, accountId: Guid.Empty));

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        (await readContext.Users.AnyAsync(u => u.Email == email, TestContext.Current.CancellationToken))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task When_AccountIdIsNotVersion4_Then_RejectsRequestWithoutCreatingAccount()
    {
        // Given
        var email = $"wrong-id-version-{Guid.NewGuid():N}@example.com";
        var client = apiFactory.CreateClient();

        // When
        var (response, _) = await client.POSTAsync<RegisterEndpoint, RegisterRequest, AuthSessionResponse>(
            NewRequest(email, accountId: Guid.CreateVersion7()));

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        (await readContext.Users.AnyAsync(u => u.Email == email, TestContext.Current.CancellationToken))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task When_AccountIdAlreadyExists_Then_Returns409AndRollsBackRegistration()
    {
        // Given
        apiFactory.GuidProvider.Generate().Returns(_ => Guid.NewGuid());
        var (existingUser, _) = await apiFactory.Services.SeedPasswordUserAsync(new byte[32]);
        var email = $"duplicate-id-{Guid.NewGuid():N}@example.com";

        await using var beforeScope = apiFactory.Services.CreateAsyncScope();
        var beforeContext = beforeScope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        var countsBefore = await RegistrationCounts.ReadAsync(beforeContext);

        var client = apiFactory.CreateClient();

        // When
        var (response, _) = await client.POSTAsync<RegisterEndpoint, RegisterRequest, AuthSessionResponse>(
            NewRequest(email, accountId: existingUser.Id));

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        await using var afterScope = apiFactory.Services.CreateAsyncScope();
        var afterContext = afterScope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        (await afterContext.Users.AnyAsync(u => u.Email == email, TestContext.Current.CancellationToken))
            .ShouldBeFalse();
        (await RegistrationCounts.ReadAsync(afterContext)).ShouldBe(countsBefore);
    }

    [Fact]
    public async Task When_RegisteringExistingEmail_Then_Returns409()
    {
        // Given
        apiFactory.GuidProvider.Generate().Returns(_ => Guid.NewGuid());
        var email = $"dup-{Guid.NewGuid():N}@example.com";
        await apiFactory.Services.SeedPasswordUserAsync(new byte[32], email: email);
        var client = apiFactory.CreateClient();

        // When
        var (response, _) = await client.POSTAsync<RegisterEndpoint, RegisterRequest, AuthSessionResponse>(
            NewRequest(email));

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task When_RegisteringNewEmail_Then_ServerHashDiffersFromClientAuthHash()
    {
        // Given — a DB leak must not yield the client authHash verbatim.
        apiFactory.GuidProvider.Generate().Returns(_ => Guid.NewGuid());
        var email = $"hash-{Guid.NewGuid():N}@example.com";
        var authHash = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var client = apiFactory.CreateClient();

        // When
        await client.POSTAsync<RegisterEndpoint, RegisterRequest, AuthSessionResponse>(NewRequest(email, authHash));

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        var credential = await readContext.PasswordCredentials
            .FirstAsync(c => c.User.Email == email, TestContext.Current.CancellationToken);
        credential.AuthHash.ShouldNotBe(authHash);
        credential.ServerHashSalt.Length.ShouldBe(16);
    }

    private static RegisterRequest NewRequest(
        string email,
        byte[]? authHash = null,
        Guid? accountId = null) => new()
    {
        AccountId = accountId ?? Guid.NewGuid(),
        Email = email,
        DisplayName = "Test User",
        PreferredLanguage = "en",
        SecurityVersion = 1,
        KdfProfileId = "identity-argon2id-password-v1",
        AuthCredential = authHash ?? Enumerable.Range(0, 32).Select(i => (byte)(i + 1)).ToArray(),
        KdfSalt = Enumerable.Range(0, 16).Select(i => (byte)(i + 2)).ToArray(),
        RecoverySalt = Enumerable.Range(0, 16).Select(i => (byte)(i + 4)).ToArray(),
        PublicKey = Enumerable.Range(0, 32).Select(i => (byte)(i + 5)).ToArray(),
        EncryptedPrivateKey = Enumerable.Range(0, 32).Select(i => (byte)(i + 6)).ToArray(),
        EncryptedPrivateKeyByRecovery = Enumerable.Range(0, 32).Select(i => (byte)(i + 7)).ToArray(),
    };

    private sealed record RegistrationCounts(
        int Organizations,
        int Roles,
        int Users,
        int OrganizationMembers,
        int PasswordCredentials,
        int VerificationTokens)
    {
        public static async Task<RegistrationCounts> ReadAsync(IdentityDbReadContext context) => new(
            await context.Organizations.CountAsync(TestContext.Current.CancellationToken),
            await context.Roles.CountAsync(TestContext.Current.CancellationToken),
            await context.Users.CountAsync(TestContext.Current.CancellationToken),
            await context.OrganizationMembers.CountAsync(TestContext.Current.CancellationToken),
            await context.PasswordCredentials.CountAsync(TestContext.Current.CancellationToken),
            await context.VerificationTokens.CountAsync(TestContext.Current.CancellationToken));
    }
}
