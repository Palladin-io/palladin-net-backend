using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using Palladin.Core.Hangfire.CronJobs;
using Palladin.Core.Types;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.History;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Infrastructure.Purge;

namespace Palladin.Module.Vault.Features;

[UsedImplicitly]
internal sealed class VaultEntryLifecycleJob(
    VaultDomainWriteContext domainWriteContext,
    IOptions<VaultHistoryOptions> historyOptions,
    IOptions<VaultEntryLifecycleOptions> lifecycleOptions,
    IOptions<EntryPurgeLedgerOptions> purgeLedgerOptions,
    EntryPurgeService purgeService,
    IClock clock) : ICronJob
{
    public string Name => "vault.entry-lifecycle";
    public string Expression => lifecycleOptions.Value.Expression;
    public bool Enabled => lifecycleOptions.Value.Enabled;

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetCurrentInstant();
        await CompactHistoryAsync(now, cancellationToken);
        if (purgeLedgerOptions.Value.Enabled)
        {
            await PurgeExpiredDeletedEntriesAsync(now, cancellationToken);
        }
    }

    private async Task CompactHistoryAsync(Instant now, CancellationToken cancellationToken)
    {
        var cutoff = now - Duration.FromDays(historyOptions.Value.MaximumAgeDays);
        while (true)
        {
            var entries = await domainWriteContext.Entries
                .Include(x => x.Keys)
                .Include(x => x.Versions)
                .Where(x => x.State == EntryState.Active || x.State == EntryState.Archived)
                .Where(x => x.Versions.Count > historyOptions.Value.MaximumVersions
                            || x.Versions.Any(version => version.Revision != x.CurrentRevision
                                                         && version.ChangedAt < cutoff))
                .OrderBy(x => x.UpdatedAt)
                .Take(lifecycleOptions.Value.BatchSize)
                .ToListAsync(cancellationToken);
            if (entries.Count == 0)
            {
                break;
            }

            var vaultIds = entries.Select(x => x.VaultId).Distinct().ToArray();
            var organizationIds = entries.Select(x => x.OrganizationId).Distinct().ToArray();
            var vaults = await domainWriteContext.Vaults
                .Where(x => organizationIds.Contains(x.OrganizationId) && vaultIds.Contains(x.Id))
                .ToDictionaryAsync(x => (x.OrganizationId, x.Id), cancellationToken);
            var floors = new Dictionary<(Guid OrganizationId, Guid VaultId), (ulong Member, ulong Discovery)>();
            foreach (var entry in entries)
            {
                var retainedRevision = entry.Versions
                    .OrderByDescending(x => x.Revision.Value)
                    .Take(historyOptions.Value.MaximumVersions)
                    .Last()
                    .Revision;
                var removed = entry.PurgeHistory(retainedRevision, cutoff);
                if (removed.RemovedVersions == 0)
                {
                    continue;
                }

                domainWriteContext.RemoveRange(removed.Versions);
                domainWriteContext.RemoveRange(removed.Keys);
                var key = (entry.OrganizationId, entry.VaultId);
                var current = floors.GetValueOrDefault(key);
                floors[key] = (
                    Math.Max(current.Member, removed.MemberSequenceFloor),
                    Math.Max(current.Discovery, removed.DiscoverySequenceFloor));
            }

            foreach (var (key, removed) in floors)
            {
                var vault = vaults[key];
                vault.AdvanceRetentionFloors(
                    new MemberSequence(Math.Max(vault.MinRetainedMemberSequence.Value, removed.Member)),
                    new DiscoverySequence(Math.Max(vault.MinRetainedDiscoverySequence.Value, removed.Discovery)),
                    vault.UpdatedBy,
                    now);
            }

            await domainWriteContext.CommitAsync(cancellationToken);
            domainWriteContext.Clear();
            if (entries.Count < lifecycleOptions.Value.BatchSize)
            {
                break;
            }
        }
    }

    private async Task PurgeExpiredDeletedEntriesAsync(Instant now, CancellationToken cancellationToken)
    {
        var cutoff = now - Duration.FromDays(lifecycleOptions.Value.RecentlyDeletedDays);
        while (true)
        {
            var due = await domainWriteContext.Entries
                .Where(x => x.State == EntryState.Deleted
                            && x.DeletedAt != null
                            && x.DeletedAt <= cutoff)
                .OrderBy(x => x.DeletedAt)
                .Take(lifecycleOptions.Value.BatchSize)
                .Select(x => new { x.OrganizationId, x.VaultId, EntryId = x.Id, x.UpdatedBy })
                .ToListAsync(cancellationToken);
            domainWriteContext.Clear();
            if (due.Count == 0)
            {
                return;
            }

            foreach (var entry in due)
            {
                await purgeService.PurgeAsync(
                    new EntryScope(entry.OrganizationId, entry.VaultId, entry.EntryId),
                    entry.UpdatedBy,
                    cutoff,
                    appendLedger: true,
                    requireDeleted: true,
                    cancellationToken);
            }

            if (due.Count < lifecycleOptions.Value.BatchSize)
            {
                return;
            }
        }
    }
}
