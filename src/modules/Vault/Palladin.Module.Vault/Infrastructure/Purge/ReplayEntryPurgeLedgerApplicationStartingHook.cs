using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Palladin.Core.Api;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Persistence;

namespace Palladin.Module.Vault.Infrastructure.Purge;

internal sealed class ReplayEntryPurgeLedgerApplicationStartingHook(
    VaultDomainReadContext readContext,
    IEntryPurgeLedger ledger,
    EntryPurgeTokenGenerator tokenGenerator,
    EntryPurgeService purgeService,
    IOptions<EntryPurgeLedgerOptions> options,
    IHostEnvironment environment) : IApplicationStartingHook
{
    internal int PageSize { get; init; } = 256;

    public async Task OnApplicationStartingAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
        {
            if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
            {
                return;
            }

            throw new InvalidOperationException("Vault Entry purge ledger cannot be disabled outside development or testing.");
        }

        var records = await ledger.ReadAllAsync(cancellationToken);
        if (records.Count == 0)
        {
            return;
        }

        var tokensByVersion = records
            .GroupBy(x => x.PurgeKeyVersion)
            .ToDictionary(
                group => group.Key,
                group => group.Select(x => x.OpaqueEntryToken).ToHashSet(StringComparer.Ordinal));
        EntryReplayCursor? cursor = null;
        while (true)
        {
            var query = readContext.Entries.IgnoreQueryFilters().AsNoTracking();
            if (cursor is not null)
            {
                query = query.Where(x =>
                    x.OrganizationId.CompareTo(cursor.OrganizationId) > 0
                    || (x.OrganizationId == cursor.OrganizationId
                        && x.VaultId.CompareTo(cursor.VaultId) > 0)
                    || (x.OrganizationId == cursor.OrganizationId
                        && x.VaultId == cursor.VaultId
                        && x.Id.CompareTo(cursor.EntryId) > 0));
            }

            var entries = await query
                .OrderBy(x => x.OrganizationId)
                .ThenBy(x => x.VaultId)
                .ThenBy(x => x.Id)
                .Select(x => new EntryReplayCursor(x.OrganizationId, x.VaultId, x.Id, x.UpdatedBy))
                .Take(PageSize)
                .ToListAsync(cancellationToken);
            if (entries.Count == 0)
            {
                return;
            }

            foreach (var entry in entries)
            {
                var scope = new EntryScope(entry.OrganizationId, entry.VaultId, entry.EntryId);
                var matched = false;
                foreach (var (version, tokens) in tokensByVersion)
                {
                    var token = await tokenGenerator.GenerateAsync(scope, version, cancellationToken);
                    if (tokens.Contains(token))
                    {
                        matched = true;
                        break;
                    }
                }

                if (matched)
                {
                    await purgeService.PurgeAsync(
                        scope,
                        entry.UpdatedBy,
                        null,
                        appendLedger: false,
                        requireDeleted: false,
                        cancellationToken);
                }
            }

            cursor = entries[^1];
        }
    }

    private sealed record EntryReplayCursor(
        Guid OrganizationId,
        Guid VaultId,
        Guid EntryId,
        Guid UpdatedBy);
}
