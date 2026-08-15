using Bogus;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared.Fakers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Palladin.Tests.Integrations.Shared.Seeders;

internal static class EntrySeeder
{
    public static async Task<IReadOnlyList<VaultEntry>> SeedEntriesAsync(
        this IServiceProvider serviceProvider,
        Guid vaultId,
        Guid createdBy,
        int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        await using var scope = serviceProvider.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
        var vault = await writeContext.Vaults.SingleAsync(x => x.Id == vaultId);
        var firstSequence = (ulong)(await writeContext.EntryVersions.CountAsync(x =>
            x.OrganizationId == vault.OrganizationId && x.VaultId == vaultId) + 1);
        var entries = Enumerable.Range(0, count)
            .Select(index =>
            {
                var builder = EntryFaker.Create(
                    organizationId: vault.OrganizationId,
                    vaultId: vaultId,
                    createdBy: createdBy);
                builder.RuleFor(x => x.Versions, (_, entry) =>
                    EntryFaker.CreateVersions(entry, firstSequence + (ulong)index));
                return builder.Generate();
            })
            .ToArray();

        var targetSequence = firstSequence + (ulong)count - 1;
        while (vault.MemberSequence.Value < targetSequence)
        {
            vault.AllocateSequences(false, createdBy, SystemClock.Instance.GetCurrentInstant());
        }

        writeContext.Entries.AddRange(entries);
        await writeContext.SaveChangesAsync();
        return entries;
    }

    public static async Task<VaultEntry> SeedEntryAsync(
        this IServiceProvider serviceProvider,
        Guid vaultId,
        Guid createdBy,
        Faker<VaultEntry>? faker = null,
        Instant? createdAt = null)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();

        var vault = await writeContext.Vaults.SingleAsync(x => x.Id == vaultId);
        var memberSequence = (ulong)(await writeContext.EntryVersions.CountAsync(x =>
            x.OrganizationId == vault.OrganizationId && x.VaultId == vaultId) + 1);
        var builder = faker ?? EntryFaker.Create(
            organizationId: vault.OrganizationId,
            vaultId: vaultId,
            createdBy: createdBy);
        builder.RuleFor(x => x.OrganizationId, vault.OrganizationId);
        builder.RuleFor(x => x.Versions, (_, entry) => EntryFaker.CreateVersions(entry, memberSequence));

        if (createdAt is not null)
        {
            builder
                .RuleFor(x => x.CreatedAt, createdAt.Value)
                .RuleFor(x => x.UpdatedAt, createdAt.Value);
        }

        var entry = builder.Generate();

        await writeContext.Entries
            .Where(x => x.VaultId == entry.VaultId && x.Id == entry.Id)
            .ExecuteDeleteAsync();

        writeContext.Entries.Add(entry);
        await writeContext.SaveChangesAsync();

        return entry;
    }
}
