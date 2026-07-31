using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Palladin.Tests.Integrations.Shared.Seeders;

internal static class GrantSeeder
{
    public static async Task<GranularGrant> SeedGranularGrantAsync(
        this IServiceProvider serviceProvider,
        GranularGrant grant)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();

        await writeContext.Grants.Where(x => x.Id == grant.Id).ExecuteDeleteAsync();
        writeContext.Grants.Add(grant);
        await writeContext.SaveChangesAsync();

        return grant;
    }

    public static async Task<FullGrant> SeedFullGrantAsync(
        this IServiceProvider serviceProvider,
        FullGrant grant)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();

        await writeContext.Grants.Where(x => x.Id == grant.Id).ExecuteDeleteAsync();
        writeContext.Grants.Add(grant);
        await writeContext.SaveChangesAsync();

        return grant;
    }
}
