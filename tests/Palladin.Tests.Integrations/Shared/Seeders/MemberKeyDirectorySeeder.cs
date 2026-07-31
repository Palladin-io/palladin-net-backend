using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Persistence;

namespace Palladin.Tests.Integrations.Shared.Seeders;

internal static class MemberKeyDirectorySeeder
{
    internal static async Task SeedMemberKeyDirectoryAsync(
        this IServiceProvider serviceProvider,
        Guid userId,
        byte[] rawPublicKey,
        uint keyVersion = 1)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var identityContext = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
        await identityContext.Users
            .Where(x => x.Id == userId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.PublicKey, rawPublicKey)
                .SetProperty(x => x.MemberKeyVersion, (uint?)keyVersion));

        var context = scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
        await context.MemberKeyDirectory.Where(x => x.UserId == userId).ExecuteDeleteAsync();
        context.MemberKeyDirectory.Add(MemberKeyDirectoryEntry.Create(
            userId,
            new MemberRecipientKeyVersion(keyVersion),
            MemberKeyFingerprint.Compute(rawPublicKey),
            rawPublicKey,
            SystemClock.Instance.GetCurrentInstant()));
        await context.SaveChangesAsync();
    }
}
