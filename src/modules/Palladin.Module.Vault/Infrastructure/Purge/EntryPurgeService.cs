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
        await using var transaction = await domainWriteContext.BeginTransactionAsync(cancellationToken);
        var entry = await domainWriteContext.LockEntry(
                scope.OrganizationId,
                scope.VaultId,
                scope.EntryId)
            .SingleOrDefaultAsync(cancellationToken);
        if (entry is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return EntryPurgeResult.NotFound;
        }

        if ((requireDeleted && entry.State != Palladin.Core.Types.EntryState.Deleted)
            || (deletedOnOrBefore is not null
                && (entry.DeletedAt is null || entry.DeletedAt > deletedOnOrBefore)))
        {
            await transaction.RollbackAsync(cancellationToken);
            return EntryPurgeResult.NotEligible;
        }

        var now = clock.GetCurrentInstant();
        if (appendLedger)
        {
            await ledger.AppendAsync(scope, now, cancellationToken);
        }

        await assetPurger.PurgeAsync(scope, cancellationToken);

        var grants = await domainWriteContext.Grants
            .Include(x => x.EncryptedReason)
            .Include(x => x.GrantEntryScopes)
            .ThenInclude(scope => scope.Envelope)
            .Where(x => x.OrganizationId == scope.OrganizationId
                        && x.VaultId == scope.VaultId
                        && (x.GrantEntryScopes.Any(grantScope => grantScope.EntryId == scope.EntryId)
                            || (x is GranularGrant && ((GranularGrant)x).EntryId == scope.EntryId)))
            .ToListAsync(cancellationToken);
        foreach (var grant in grants)
        {
            grant.RemoveEntryScope(scope.EntryId, now);
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
        var vault = await domainWriteContext.Vaults.SingleAsync(
            x => x.OrganizationId == scope.OrganizationId && x.Id == scope.VaultId,
            cancellationToken);
        vault.AdvanceRetentionFloors(
            new MemberSequence(Math.Max(vault.MinRetainedMemberSequence.Value, memberFloor)),
            new DiscoverySequence(Math.Max(vault.MinRetainedDiscoverySequence.Value, discoveryFloor)),
            actorId,
            now);

        domainWriteContext.Remove(entry);
        await domainWriteContext.CommitAsync(transaction, cancellationToken);
        domainWriteContext.Clear();
        return EntryPurgeResult.Purged;
    }
}
