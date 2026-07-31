using Palladin.Module.Vault.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using VaultUser = Palladin.Module.Vault.Domain.User;

namespace Palladin.Tests.Integrations.Shared.Seeders;

internal static class VaultUserSeeder
{
    public static async Task<VaultUser> SeedVaultUserAsync(
        this IServiceProvider serviceProvider,
        Guid id,
        string displayName)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();

        var now = SystemClock.Instance.GetCurrentInstant();
        var user = VaultUser.Create(id, displayName, now, now);

        await writeContext.Users.Where(x => x.Id == user.Id).ExecuteDeleteAsync();
        writeContext.Users.Add(user);
        await writeContext.SaveChangesAsync();

        return user;
    }
}
