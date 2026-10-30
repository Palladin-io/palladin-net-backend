using Microsoft.EntityFrameworkCore;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Crypto;

namespace Palladin.Module.Vault.Infrastructure.Persistence;

internal sealed record CurrentApprover(Guid MemberId, uint KeyVersion, byte[] Fingerprint, byte[] PublicKey);

// The authoritative approver set: Identity-fed GrantManage eligibility joined with the current Member
// X25519 key directory. A wrap is served only to a current approver whose current key it addresses.
internal sealed class ApproverKeySetState
{
    private readonly Dictionary<Guid, CurrentApprover> _approvers;

    internal ApproverKeySetState(OrganizationApproverKeySet? keySet, IReadOnlyList<CurrentApprover> approvers)
    {
        KeySet = keySet;
        Approvers = approvers;
        _approvers = approvers.ToDictionary(x => x.MemberId);
        Digest = AgentSubmissionCrypto.ComputeApproverSetDigest(
            approvers.Select(x => (x.MemberId, x.KeyVersion, x.Fingerprint)));
    }

    internal OrganizationApproverKeySet? KeySet { get; }
    internal IReadOnlyList<CurrentApprover> Approvers { get; }
    internal byte[] Digest { get; }

    internal bool HasServedApprover => Approvers.Any(x => IsServed(x.MemberId));

    internal bool RotationRequired => KeySet is not null
        && KeySet.Wraps.Where(wrap => wrap.KeyVersion == KeySet.KeyVersion)
            .Any(wrap => !_approvers.TryGetValue(wrap.MemberId, out var approver) || !Addresses(wrap, approver));

    internal bool HasRemovedApproverWrap => KeySet is not null
        && KeySet.Wraps.Any(wrap => wrap.KeyVersion == KeySet.KeyVersion && !_approvers.ContainsKey(wrap.MemberId));

    internal bool IsServed(Guid memberId) =>
        KeySet is not null
        && _approvers.TryGetValue(memberId, out var approver)
        && KeySet.CurrentWrapsOf(memberId).Count(wrap => Addresses(wrap, approver)) == 2;

    internal bool HasCurrentWrap(Guid memberId) => KeySet?.CurrentWrapsOf(memberId).Any() == true;

    internal IEnumerable<ApproverKeyWrap> ServedWrapsOf(Guid memberId) =>
        IsServed(memberId) ? KeySet!.CurrentWrapsOf(memberId) : [];

    internal bool TryGet(Guid memberId, out CurrentApprover approver) =>
        _approvers.TryGetValue(memberId, out approver!);

    private static bool Addresses(ApproverKeyWrap wrap, CurrentApprover approver) =>
        wrap.RecipientKeyVersion.Value == approver.KeyVersion
        && wrap.RecipientFingerprint.AsSpan().SequenceEqual(approver.Fingerprint);
}

internal static class ApproverQueries
{
    public static Task<ApproverKeySetState> LoadApproverStateAsync(
        this VaultDomainReadContext ctx, Guid organizationId, CancellationToken ct) =>
        LoadAsync(ctx.OrganizationApproverKeySets, ctx.OrganizationApprovers, ctx.MemberKeyDirectory,
            organizationId, ct);

    public static Task<ApproverKeySetState> LoadApproverStateAsync(
        this VaultDomainWriteContext ctx, Guid organizationId, CancellationToken ct) =>
        LoadAsync(ctx.OrganizationApproverKeySets, ctx.OrganizationApprovers, ctx.MemberKeyDirectory,
            organizationId, ct);

    private static async Task<ApproverKeySetState> LoadAsync(
        IQueryable<OrganizationApproverKeySet> keySets,
        IQueryable<OrganizationApprover> eligibility,
        IQueryable<MemberKeyDirectoryEntry> directory,
        Guid organizationId,
        CancellationToken ct)
    {
        var keySet = await keySets
            .Include(x => x.Wraps)
            .SingleOrDefaultAsync(x => x.OrganizationId == organizationId, ct);
        var approvers = await eligibility
            .Where(x => x.OrganizationId == organizationId && x.IsApprover)
            .Join(directory, approver => approver.UserId, key => key.UserId, (_, key) => key)
            .OrderBy(key => key.UserId)
            .Take(AgentSubmissionLimits.MaximumApprovers + 1)
            .ToListAsync(ct);
        if (approvers.Count > AgentSubmissionLimits.MaximumApprovers)
        {
            throw new AgentSubmissionConflictException("approver-limit-exceeded");
        }

        return new ApproverKeySetState(keySet, approvers
            .Select(key => new CurrentApprover(key.UserId, key.KeyVersion.Value, key.Fingerprint, key.PublicKey))
            .ToList());
    }
}
