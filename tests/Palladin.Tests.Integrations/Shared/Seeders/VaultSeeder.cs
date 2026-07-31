using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared.Fakers;

namespace Palladin.Tests.Integrations.Shared.Seeders;

internal static class VaultSeeder
{
    public static async Task<Vault> SeedVaultAsync(
        this IServiceProvider serviceProvider,
        Guid organizationId,
        Guid userId,
        Vault? vault = null,
        bool isDefault = false)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
        vault ??= VaultFaker.Create(organizationId: organizationId, createdBy: userId, isDefault: isDefault);

        await writeContext.Vaults.Where(x => x.Id == vault.Id).ExecuteDeleteAsync();
        writeContext.Vaults.Add(vault);
        await writeContext.SaveChangesAsync();
        return vault;
    }
}
