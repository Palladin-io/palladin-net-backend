using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Persistence;

namespace Palladin.Module.Vault.Infrastructure.Purge;

internal enum EntryPurgeResult
{
    NotFound,
    NotEligible,
    Purged,
}

internal sealed class EntryPurgeService(
    VaultDomainWriteContext domainWriteContext,
    IEntryPurgeLedger ledger,
    IEntryAssetPurger assetPurger,
    IClock clock)
{
    internal async Task<EntryPurgeResult> PurgeAsync(
        EntryScope scope,
        Guid actorId,
        Instant? deletedOnOrBefore,
        bool appendLedger,
        bool requireDeleted,
        CancellationToken cancellationToken)
    {
        var entry = await domainWriteContext.Entries
            .IgnoreQueryFilters()
            .Where(x => x.OrganizationId == scope.OrganizationId
                        && x.VaultId == scope.VaultId
                        && x.Id == scope.EntryId)
            .SingleOrDefaultAsync(cancellationToken);
        if (entry is null)
        {
            return EntryPurgeResult.NotFound;
        }

        if (!entry.IsPurging
            && ((requireDeleted && entry.State != Palladin.Core.Types.EntryState.Deleted)
                || (deletedOnOrBefore is not null
                    && (entry.DeletedAt is null || entry.DeletedAt > deletedOnOrBefore))))
        {
            return EntryPurgeResult.NotEligible;
        }

        if (!entry.IsPurging)
        {
            entry.BeginPurge(actorId, appendLedger, clock.GetCurrentInstant());
            await domainWriteContext.CommitAsync(cancellationToken);
        }

        var purgeRequestedAt = entry.PurgeRequestedAt
                               ?? throw new InvalidOperationException("Purging Entry is missing its durable request time.");
        if (entry.PurgeLedgerRequired)
        {
            await ledger.AppendAsync(scope, purgeRequestedAt, cancellationToken);
        }

        // Both operations are idempotent and intentionally execute without database locks.
        await assetPurger.PurgeAsync(scope, cancellationToken);

        domainWriteContext.Clear();
        entry = await domainWriteContext.Entries
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(x => x.OrganizationId == scope.OrganizationId
                                       && x.VaultId == scope.VaultId
                                       && x.Id == scope.EntryId,
                cancellationToken);
        if (entry is null)
        {
            return EntryPurgeResult.Purged;
        }

        var now = clock.GetCurrentInstant();
        var purgeRequestedBy = entry.PurgeRequestedBy
                               ?? throw new InvalidOperationException("Purging Entry is missing its durable actor.");

        var grants = await domainWriteContext.Grants
            .Include(x => x.EncryptedReason)
            .Include(x => x.GrantEntryScopes)
            .ThenInclude(scope => scope.Envelope)
            .Include(x => x.ScriptExecutionScopes)
            .Where(x => x.OrganizationId == scope.OrganizationId
                        && x.VaultId == scope.VaultId
                        && (x.GrantEntryScopes.Any(grantScope => grantScope.EntryId == scope.EntryId)
                            || x.ScriptExecutionScopes.Any(scriptScope => scriptScope.EntryId == scope.EntryId)
                            || (x is GranularGrant && ((GranularGrant)x).EntryId == scope.EntryId)))
            .ToListAsync(cancellationToken);
        var agentIds = grants.Select(x => x.AgentId).Distinct().ToArray();
        var agentNames = await domainWriteContext.Agents
            .Where(x => x.OrganizationId == scope.OrganizationId && agentIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
        foreach (var grant in grants)
        {
            grant.RemoveEntryScope(
                scope.EntryId,
                new GrantNames(
                    agentNames.GetValueOrDefault(grant.AgentId) ?? GrantNames.UnknownAgent,
                    GrantNames.UnknownEntry,
                    string.Empty,
                    GrantNames.SystemActor),
                now);
        }

        // Keep required child relationships unloaded while deleting the aggregate. The database
        // cascades the entry deletion to both keys and versions; tracking both branches would make
        // EF sever the required version-to-key relationship before the database can do so.
        var retainedSequences = await domainWriteContext.EntryVersions
            .Where(x => x.OrganizationId == scope.OrganizationId
                        && x.VaultId == scope.VaultId
                        && x.EntryId == scope.EntryId)
            .Select(x => new
            {
                x.MemberSequence,
                x.DiscoverySequence,
            })
            .ToListAsync(cancellationToken);
        var memberFloor = retainedSequences.Max(x => x.MemberSequence.Value);
        var discoveryFloor = retainedSequences
            .Where(x => x.DiscoverySequence is not null)
            .Select(x => x.DiscoverySequence!.Value.Value)
            .DefaultIfEmpty(0UL)
            .Max();
        var vault = await domainWriteContext.Vaults.IgnoreQueryFilters().SingleAsync(
            x => x.OrganizationId == scope.OrganizationId && x.Id == scope.VaultId,
            cancellationToken);
        vault.AdvanceRetentionFloors(
            new MemberSequence(Math.Max(vault.MinRetainedMemberSequence.Value, memberFloor)),
            new DiscoverySequence(Math.Max(vault.MinRetainedDiscoverySequence.Value, discoveryFloor)),
            purgeRequestedBy,
            now);

        domainWriteContext.Remove(entry);
        await domainWriteContext.CommitAsync(cancellationToken);
        domainWriteContext.Clear();
        return EntryPurgeResult.Purged;
    }
}
