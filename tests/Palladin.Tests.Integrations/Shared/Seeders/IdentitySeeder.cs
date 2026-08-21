using Bogus;
using Palladin.Core.Security;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Contracts.ValueObjects;
using Palladin.Module.Identity.Domain.Enums;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Infrastructure.Jwt;
using Palladin.Tests.Integrations.Shared.Fakers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Palladin.Tests.Integrations.Shared.Seeders;

internal static class IdentitySeeder
{
    public static async Task<(User User, Organization Organization, Role Role)> SeedUserAsync(
        this IServiceProvider serviceProvider,
        Faker<User>? userFaker = null,
        Faker<Organization>? organizationFaker = null)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();

        var organization = (organizationFaker ?? OrganizationFaker.Create()).Generate();

        await writeContext.Organizations
            .Where(x => x.Id == organization.Id)
            .ExecuteDeleteAsync();

        writeContext.Organizations.Add(organization);

        var role = Role.CreateAdministrator(Guid.NewGuid(), organization.Id, SystemClock.Instance.GetCurrentInstant());
        writeContext.Roles.Add(role);
        writeContext.Roles.Add(Role.CreateDefaultUser(
            Guid.NewGuid(), organization.Id, SystemClock.Instance.GetCurrentInstant()));

        var effectiveUserFaker = userFaker ?? UserFaker.Create().RuleFor(x => x.EmailVerified, true);
        var user = effectiveUserFaker
            .RuleFor(x => x.OrganizationId, organization.Id)
            .Generate();

        await writeContext.Users
            .Where(x => x.Id == user.Id)
            .ExecuteDeleteAsync();

        writeContext.Users.Add(user);

        writeContext.OrganizationMembers.Add(OrganizationMember.CreateOwner(
            organization.Id, user.Id, role, SystemClock.Instance.GetCurrentInstant()));

        await writeContext.SaveChangesAsync();

        return (user, organization, role);
    }

    public static async Task<User> SeedAdditionalOrganizationMemberAsync(
        this IServiceProvider serviceProvider,
        Guid organizationId,
        Faker<User>? userFaker = null)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();

        var role = await writeContext.Roles.FirstOrDefaultAsync(
            r => r.OrganizationId == organizationId && r.Name == "Test Member");
        if (role is null)
        {
            role = Role.Create(
                Guid.NewGuid(), organizationId, "Test Member", Permission.GrantManage,
                isSystem: false, SystemClock.Instance.GetCurrentInstant());
            writeContext.Roles.Add(role);
        }

        var effectiveUserFaker = userFaker ?? UserFaker.Create().RuleFor(x => x.EmailVerified, true);
        var user = effectiveUserFaker
            .RuleFor(x => x.OrganizationId, organizationId)
            .Generate();

        await writeContext.Users
            .Where(x => x.Id == user.Id)
            .ExecuteDeleteAsync();

        writeContext.Users.Add(user);
        writeContext.OrganizationMembers.Add(OrganizationMember.Create(
            organizationId, user.Id, role,
            user.DisplayName, user.Email, SystemClock.Instance.GetCurrentInstant()));

        await writeContext.SaveChangesAsync();

        return user;
    }

    public static async Task SeedOAuthConnectionAsync(
        this IServiceProvider serviceProvider,
        Guid userId,
        AuthProvider provider,
        string providerUserId,
        string email)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();

        await writeContext.OAuthConnections
            .Where(x => x.Provider == provider && x.ProviderUserId == providerUserId)
            .ExecuteDeleteAsync();

        var connection = OAuthConnection.Create(Guid.NewGuid(), userId, provider, providerUserId, email, SystemClock.Instance.GetCurrentInstant());
        writeContext.OAuthConnections.Add(connection);
        await writeContext.SaveChangesAsync();
    }

    public static async Task<RefreshToken> SeedRefreshTokenAsync(
        this IServiceProvider serviceProvider,
        Guid userId,
        string rawToken,
        Instant? expiresAt = null,
        Instant? revokedAt = null,
        Guid? id = null,
        Guid? replacedByTokenId = null)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();

        var tokenHash = TokenService.HashToken(rawToken);

        await writeContext.RefreshTokens
            .Where(x => x.TokenHash == tokenHash)
            .ExecuteDeleteAsync();

        var now = SystemClock.Instance.GetCurrentInstant();
        var organizationId = await writeContext.Users
            .Where(u => u.Id == userId)
            .Select(u => u.OrganizationId)
            .FirstAsync();
        var authorizationVersion = await writeContext.OrganizationMembers
            .Where(m => m.OrganizationId == organizationId && m.UserId == userId)
            .Select(m => m.AuthorizationVersion)
            .SingleAsync();
        var refreshToken = RefreshToken.Create(
            id ?? Guid.NewGuid(), userId, organizationId, tokenHash,
            authorizationVersion, expiresAt ?? now.Plus(Duration.FromDays(30)), now);

        if (revokedAt is not null)
        {
            refreshToken.Revoke(revokedAt.Value, replacedByTokenId);
        }

        writeContext.RefreshTokens.Add(refreshToken);
        await writeContext.SaveChangesAsync();

        return refreshToken;
    }
}
