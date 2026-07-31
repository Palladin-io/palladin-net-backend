using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure.PasswordAuth;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Infrastructure.Totp;
using Palladin.Tests.Integrations.Shared.Fakers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Palladin.Tests.Integrations.Shared.Seeders;

internal static class IdentityAuthSeeder
{
    // Seeds an org + admin user with a password credential whose server hash is produced by the real
    // IPasswordHasher, so the login endpoint verifies the same authHash.
    public static async Task<(User User, Organization Organization)> SeedPasswordUserAsync(
        this IServiceProvider serviceProvider,
        byte[] authHash,
        byte[]? authSalt = null,
        string? email = null,
        bool emailVerified = false,
        ushort securityVersion = IdentityKdfProfiles.CurrentSecurityVersion,
        bool isOnboarded = true,
        Guid? userId = null)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var now = SystemClock.Instance.GetCurrentInstant();
        var clientSalt = authSalt ?? new byte[16];

        var organization = OrganizationFaker.Create().Generate();
        await writeContext.Organizations.Where(x => x.Id == organization.Id).ExecuteDeleteAsync();
        writeContext.Organizations.Add(organization);

        var role = Role.CreateAdministrator(Guid.NewGuid(), organization.Id, now);
        writeContext.Roles.Add(role);

        var user = UserFaker.Create(userId, organization.Id, email)
            .RuleFor(x => x.EmailVerified, emailVerified)
            .RuleFor(x => x.IsOnboarded, isOnboarded)
            .RuleFor(x => x.Salt, isOnboarded ? clientSalt : null)
            .RuleFor(x => x.EncryptedPrivateKey, isOnboarded
                ? Enumerable.Range(0, 32).Select(i => (byte)(i + 0x20)).ToArray()
                : null)
            .RuleFor(x => x.RecoverySalt, isOnboarded
                ? Enumerable.Range(0, 16).Select(i => (byte)(i + 0x30)).ToArray()
                : null)
            .RuleFor(x => x.EncryptedPrivateKeyByRecovery, isOnboarded
                ? Enumerable.Range(0, 32).Select(i => (byte)(i + 0x40)).ToArray()
                : null)
            .RuleFor(x => x.PrivateKeyWrapRevision, isOnboarded ? 1u : 0u)
            .RuleFor(x => x.SecurityVersion, securityVersion)
            .RuleFor(x => x.MinimumSecurityVersion, securityVersion)
            .RuleFor(x => x.KdfProfileId, IdentityKdfProfiles.CurrentProfileId)
            .RuleFor(x => x.CredentialRevision, 1u)
            .Generate();
        await writeContext.Users.Where(x => x.Id == user.Id).ExecuteDeleteAsync();
        writeContext.Users.Add(user);
        writeContext.OrganizationMembers.Add(OrganizationMember.CreateOwner(
            organization.Id, user.Id, role, now));

        var (serverHash, serverSalt) = hasher.Hash(authHash);
        writeContext.PasswordCredentials.Add(
            PasswordCredential.Create(
                user.Id,
                serverHash,
                clientSalt,
                serverSalt,
                now));

        await writeContext.SaveChangesAsync();

        return (user, organization);
    }

    // Enables a confirmed TOTP factor with a known secret (optionally a single recovery code).
    public static async Task SeedTotpCredentialAsync(
        this IServiceProvider serviceProvider,
        Guid userId,
        string secret,
        string? recoveryCodeHash = null)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
        var now = SystemClock.Instance.GetCurrentInstant();

        var credential = TotpCredential.StartEnrollment(userId, secret, now);
        credential.Confirm(0, now);
        writeContext.TotpCredentials.Add(credential);

        if (recoveryCodeHash is not null)
        {
            writeContext.TotpRecoveryCodes.Add(TotpRecoveryCode.Create(Guid.NewGuid(), userId, recoveryCodeHash));
        }

        await writeContext.SaveChangesAsync();
    }
}
