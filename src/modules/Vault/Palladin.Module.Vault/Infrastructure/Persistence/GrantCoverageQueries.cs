using Palladin.Core.Types;
using Palladin.Module.Vault.Domain;
using Microsoft.EntityFrameworkCore;

namespace Palladin.Module.Vault.Infrastructure.Persistence;

// Coverage invariant: an agent has at most one active grant covering a given entry.
// "Coverage" of an entry = an active GRANULAR grant on that entry OR an active FULL grant on the
// entry's vault. Used to enforce the invariant on active-grant creation and to resolve a matching
// pending request. Read-side helpers — callers wrap writes in their own transaction.
internal static class GrantCoverageQueries
{
    // True if the agent already has an active grant covering the entry (active GRANULAR on the entry,
    // or active FULL on the vault). Optionally ignore a specific grant id (e.g. the one being approved).
    public static async Task<bool> HasActiveEntryCoverageAsync(
        this VaultDomainReadContext ctx,
        Guid agentId,
        uint agentAccessEpoch,
        Guid vaultId,
        Guid entryId,
        Guid? excludingGrantId,
        CancellationToken ct)
    {
        var currentRevision = await ctx.Entries
            .Where(entry => entry.VaultId == vaultId
                            && entry.Id == entryId
                            && entry.State == EntryState.Active)
            .Select(entry => (ulong?)entry.CurrentRevision.Value)
            .SingleOrDefaultAsync(ct);

        if (currentRevision is null)
        {
            return false;
        }

        return await ctx.Grants.AnyAsync(g =>
            g.AgentId == agentId
            && g.AgentAccessEpoch == agentAccessEpoch
            && g.VaultId == vaultId
            && g.Status == GrantStatus.Active
            && (excludingGrantId == null || g.Id != excludingGrantId)
            && ((g is FullGrant && g.AgentWrappedVaultKey != null)
                || (g is GranularGrant && g.GrantEntryScopes.Any(scope =>
                    scope.EntryId == entryId
                    && scope.Envelope != null
                    && scope.Envelope.EntryRevision == currentRevision.Value))),
            ct);
    }

    // True if the agent already has an active FULL grant on the vault — a real duplicate that blocks a
    // new FULL grant (409). Active GRANULAR grants do NOT block: a FULL grant promotes/supersedes them
    // (see LoadActiveGranularInVaultAsync).
    public static Task<bool> HasActiveFullCoverageAsync(
        this VaultDomainReadContext ctx,
        Guid agentId,
        uint agentAccessEpoch,
        Guid vaultId,
        CancellationToken ct) =>
        ctx.Grants.AnyAsync(g =>
            g.AgentId == agentId
            && g.AgentAccessEpoch == agentAccessEpoch
            && g.VaultId == vaultId
            && g.Status == GrantStatus.Active
            && g is FullGrant,
            ct);

    // The agent's active GRANULAR grants in the vault — superseded (revoked-by-system) when a FULL grant
    // is created, so exactly one active grant covers each entry. Read on the write context so the caller
    // can mutate the tracked entities in the same transaction.
    public static Task<List<GranularGrant>> LoadActiveGranularInVaultAsync(
        this VaultDomainWriteContext ctx,
        Guid agentId,
        uint agentAccessEpoch,
        Guid vaultId,
        CancellationToken ct) =>
        ctx.Grants
            .OfType<GranularGrant>()
            .Include(g => g.GrantEntryScopes)
            .ThenInclude(scope => scope.Envelope)
            .Where(g =>
                g.AgentId == agentId
                && g.AgentAccessEpoch == agentAccessEpoch
                && g.VaultId == vaultId
                && g.Status == GrantStatus.Active)
            .ToListAsync(ct);

    public static Task<List<GranularGrant>> LoadActiveGranularInVaultPageAsync(
        this VaultDomainWriteContext ctx,
        Guid agentId,
        uint agentAccessEpoch,
        Guid vaultId,
        Guid? afterGrantId,
        int pageSize,
        CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        var query = ctx.Grants
            .OfType<GranularGrant>()
            .Include(g => g.GrantEntryScopes)
            .ThenInclude(scope => scope.Envelope)
            .Where(g =>
                g.AgentId == agentId
                && g.AgentAccessEpoch == agentAccessEpoch
                && g.VaultId == vaultId
                && g.Status == GrantStatus.Active);
        if (afterGrantId is not null)
        {
            query = query.Where(g => g.Id.CompareTo(afterGrantId.Value) > 0);
        }

        return query
            .OrderBy(g => g.Id)
            .Take(pageSize)
            .ToListAsync(ct);
    }

    // The agent's pending GRANULAR request for this exact entry, if any — resolved (approved) instead of
    // creating a duplicate active grant, so the pending list clears. Read on the write context so the
    // caller can mutate the returned tracked entity.
    public static Task<GranularGrant?> FindPendingForEntryAsync(
        this VaultDomainWriteContext ctx,
        Guid agentId,
        uint agentAccessEpoch,
        Guid vaultId,
        Guid entryId,
        CancellationToken ct) =>
        ctx.Grants
            .OfType<GranularGrant>()
            .Include(g => g.EncryptedReason)
            .FirstOrDefaultAsync(g =>
                g.AgentId == agentId
                && g.AgentAccessEpoch == agentAccessEpoch
                && g.VaultId == vaultId
                && g.EntryId == entryId
                && g.Status == GrantStatus.Pending,
                ct);
}
