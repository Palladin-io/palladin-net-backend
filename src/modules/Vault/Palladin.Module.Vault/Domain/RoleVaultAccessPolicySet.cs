using NodaTime;
using Palladin.Core.Security;
using Palladin.Core.Events;
using Palladin.Module.Vault.Contracts.Events;

namespace Palladin.Module.Vault.Domain;

internal sealed record RoleVaultAccessPolicyChange(
    IReadOnlyList<Guid> AddedVaultIds,
    IReadOnlyList<Guid> RemovedVaultIds)
{
    internal bool HasChanges => AddedVaultIds.Count > 0 || RemovedVaultIds.Count > 0;
}

internal sealed class RoleVaultAccessPolicySet : EventEntityBase
{
    public Guid OrganizationId { get; private set; }
    public Guid RoleId { get; private set; }
    public ulong Revision { get; private set; }
    public ulong AuthorizedRoleRevision { get; private set; }
    public Permission AuthorizedByPermissions { get; private set; }
    public Guid? LastOperationId { get; private set; }
    public Guid UpdatedBy { get; private set; }
    public Instant UpdatedAt { get; private set; }

    public OrganizationRoleDirectoryEntry Role { get; private set; } = null!;
    public ICollection<RoleVaultAccessPolicy> Policies { get; private set; } = [];

    private RoleVaultAccessPolicySet() { }

    internal static RoleVaultAccessPolicySet Create(
        Guid organizationId,
        Guid roleId,
        IReadOnlyCollection<Guid> vaultIds,
        Guid operationId,
        Guid updatedBy,
        ulong authorizedRoleRevision,
        Permission authorizedByPermissions,
        Instant now)
    {
        var normalizedVaultIds = Normalize(vaultIds);
        var policySet = new RoleVaultAccessPolicySet
        {
            OrganizationId = organizationId,
            RoleId = roleId,
            Revision = 1,
            AuthorizedRoleRevision = authorizedRoleRevision,
            AuthorizedByPermissions = authorizedByPermissions,
            LastOperationId = operationId,
            UpdatedBy = updatedBy,
            UpdatedAt = now,
            Policies = normalizedVaultIds
                .Select(vaultId => RoleVaultAccessPolicy.Create(organizationId, roleId, vaultId))
                .ToList(),
        };

        policySet.EmitChange(operationId, updatedBy, now);
        return policySet;
    }

    internal RoleVaultAccessPolicyChange Preview(IReadOnlyCollection<Guid> vaultIds)
    {
        var current = Policies.Select(policy => policy.VaultId).ToHashSet();
        var requested = vaultIds.ToHashSet();
        return new RoleVaultAccessPolicyChange(
            requested.Except(current).Order().ToArray(),
            current.Except(requested).Order().ToArray());
    }

    internal RoleVaultAccessPolicyChange Replace(
        IReadOnlyCollection<Guid> vaultIds,
        Guid operationId,
        Guid updatedBy,
        Instant now)
    {
        var change = Preview(vaultIds);
        if (!change.HasChanges)
        {
            return change;
        }

        if (Revision == ulong.MaxValue)
        {
            throw new InvalidOperationException("Role Vault access policy revision namespace is exhausted.");
        }

        foreach (var removedVaultId in change.RemovedVaultIds)
        {
            Policies.Remove(Policies.Single(policy => policy.VaultId == removedVaultId));
        }

        foreach (var addedVaultId in change.AddedVaultIds)
        {
            Policies.Add(RoleVaultAccessPolicy.Create(OrganizationId, RoleId, addedVaultId));
        }

        Revision++;
        LastOperationId = operationId;
        UpdatedBy = updatedBy;
        UpdatedAt = now;
        EmitChange(operationId, updatedBy, now);
        return change;
    }

    internal void Reconcile(Guid operationId, Guid changedBy, Instant now)
    {
        if (Revision == ulong.MaxValue)
        {
            throw new InvalidOperationException("Role Vault access policy revision namespace is exhausted.");
        }

        Revision++;
        LastOperationId = operationId;
        UpdatedBy = changedBy;
        UpdatedAt = now;
        EmitChange(operationId, changedBy, now);
    }

    internal void BindRoleAuthorization(
        ulong roleRevision,
        Permission authorizedByPermissions)
    {
        if (roleRevision < AuthorizedRoleRevision)
        {
            throw new InvalidOperationException("Role Vault access authorization revision cannot move backwards.");
        }

        AuthorizedRoleRevision = roleRevision;
        AuthorizedByPermissions = authorizedByPermissions;
    }

    private void EmitChange(
        Guid operationId,
        Guid changedBy,
        Instant now) =>
        AddOrReplaceEvent(new RoleVaultAccessPolicyChangedEvent(
            OrganizationId,
            RoleId,
            operationId,
            Revision,
            changedBy,
            Policies.Select(x => x.VaultId).Order().ToArray(),
            now));

    private static Guid[] Normalize(IEnumerable<Guid> vaultIds) =>
        vaultIds.Distinct().Order().ToArray();
}
