using Microsoft.EntityFrameworkCore;
using Palladin.Core.Guid;
using Palladin.Core.Security;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Persistence;
using NodaTime;

namespace Palladin.Module.Vault.Features;

internal sealed class RoleVaultAccessReconciler(
    VaultDomainWriteContext domainWriteContext,
    IGuidProvider guidProvider)
{
    private const int MaximumPoliciesPerVaultDeletion = 1000;

    internal async Task ReconcileRoleAuthorizationAsync(
        OrganizationRoleDirectoryEntry role,
        Instant now,
        CancellationToken cancellationToken)
    {
        var policy = await domainWriteContext.RoleVaultAccessPolicySets
            .Include(x => x.Policies)
            .SingleOrDefaultAsync(
                x => x.OrganizationId == role.OrganizationId && x.RoleId == role.RoleId,
                cancellationToken);
        if (policy is null || policy.AuthorizedRoleRevision >= role.SourceRevision)
        {
            return;
        }

        if ((role.Permissions & policy.AuthorizedByPermissions) == role.Permissions)
        {
            policy.BindRoleAuthorization(role.SourceRevision, policy.AuthorizedByPermissions);
            return;
        }

        await SupersedeCurrentOperationAsync(policy, now, cancellationToken);
        if (policy.Policies.Count == 0)
        {
            policy.BindRoleAuthorization(role.SourceRevision, Permission.None);
            return;
        }

        var selectedVaultIds = policy.Policies.Select(x => x.VaultId).ToArray();
        var activeRoleMembers = await domainWriteContext.OrganizationMemberRoleSets
            .Where(x => x.OrganizationId == role.OrganizationId
                        && x.IsActive
                        && x.RoleIds.Contains(role.RoleId))
            .Select(x => new { x.UserId, x.RoleIds })
            .ToArrayAsync(cancellationToken);
        var activeRoleMemberIds = activeRoleMembers.Select(x => x.UserId).ToArray();
        var existingMemberships = await domainWriteContext.VaultMembers
            .Where(x => x.OrganizationId == role.OrganizationId
                        && activeRoleMemberIds.Contains(x.UserId)
                        && selectedVaultIds.Contains(x.VaultId))
            .Select(x => new { x.UserId, x.VaultId })
            .ToArrayAsync(cancellationToken);
        var existingMembershipSet = existingMemberships
            .Select(x => (x.UserId, x.VaultId))
            .ToHashSet();
        var remainingRoleIds = activeRoleMembers
            .SelectMany(member => member.RoleIds)
            .Where(roleId => roleId != role.RoleId)
            .Distinct()
            .ToArray();
        var remainingRolePolicies = await domainWriteContext.RoleVaultAccessPolicies
            .Where(x => x.OrganizationId == role.OrganizationId
                        && remainingRoleIds.Contains(x.RoleId)
                        && selectedVaultIds.Contains(x.VaultId))
            .Select(x => new { x.RoleId, x.VaultId })
            .ToArrayAsync(cancellationToken);
        var remainingRolePolicySet = remainingRolePolicies
            .Select(x => (x.RoleId, x.VaultId))
            .ToHashSet();
        var affectedMembers = activeRoleMembers.Count(member => selectedVaultIds.Any(vaultId =>
            existingMembershipSet.Contains((member.UserId, vaultId))
            && member.RoleIds
                .Where(roleId => roleId != role.RoleId)
                .All(roleId => !remainingRolePolicySet.Contains((roleId, vaultId)))));
        var operation = RoleVaultAccessOperation.Create(
            role.OrganizationId,
            guidProvider.Generate(),
            role.RoleId,
            0,
            affectedMembers,
            activeRoleMembers.Length - affectedMembers,
            Guid.Empty,
            now);
        domainWriteContext.Add(operation);
        policy.Replace([], operation.Id, Guid.Empty, now);
        policy.BindRoleAuthorization(role.SourceRevision, Permission.None);
    }

    internal async Task ReconcileMemberRoleSetAsync(
        Guid organizationId,
        Guid userId,
        IReadOnlyCollection<Guid> previousRoleIds,
        bool wasActive,
        IReadOnlyCollection<Guid> currentRoleIds,
        bool isActive,
        Instant now,
        CancellationToken cancellationToken)
    {
        var affectedRoleIds = previousRoleIds
            .Concat(currentRoleIds)
            .Distinct()
            .Where(roleId => (wasActive && previousRoleIds.Contains(roleId))
                             != (isActive && currentRoleIds.Contains(roleId)))
            .ToArray();
        if (affectedRoleIds.Length == 0)
        {
            return;
        }

        // Serialize assignment-driven reconciliation with policy replacement through the same
        // optimistic role fence. If both read before either commits, one writer loses and retries;
        // neither can commit an operation calculated from the other writer's invisible state.
        var affectedRoles = await domainWriteContext.OrganizationRoleDirectory
            .Where(x => x.OrganizationId == organizationId && affectedRoleIds.Contains(x.RoleId))
            .ToListAsync(cancellationToken);
        foreach (var affectedRole in affectedRoles)
        {
            affectedRole.FencePolicyMutation();
        }

        var allRelevantRoleIds = previousRoleIds.Concat(currentRoleIds).Distinct().ToArray();
        var policies = await domainWriteContext.RoleVaultAccessPolicySets
            .Include(x => x.Policies)
            .Where(x => x.OrganizationId == organizationId && allRelevantRoleIds.Contains(x.RoleId))
            .ToListAsync(cancellationToken);
        var policiesByRole = policies.ToDictionary(
            x => x.RoleId,
            x => x.Policies.Select(policy => policy.VaultId).ToHashSet());
        var previousEffectiveVaultIds = wasActive
            ? previousRoleIds
                .SelectMany(roleId => policiesByRole.GetValueOrDefault(roleId) ?? [])
                .ToHashSet()
            : [];
        var currentEffectiveVaultIds = isActive
            ? currentRoleIds
                .SelectMany(roleId => policiesByRole.GetValueOrDefault(roleId) ?? [])
                .ToHashSet()
            : [];
        var newlyEffectiveVaultIds = currentEffectiveVaultIds.Except(previousEffectiveVaultIds).ToHashSet();
        var noLongerEffectiveVaultIds = previousEffectiveVaultIds.Except(currentEffectiveVaultIds).ToHashSet();
        var selectedVaultIds = policies
            .SelectMany(x => x.Policies)
            .Select(x => x.VaultId)
            .Distinct()
            .ToArray();
        var memberships = await domainWriteContext.VaultMembers
            .Where(x => x.OrganizationId == organizationId
                        && x.UserId == userId
                        && selectedVaultIds.Contains(x.VaultId))
            .Select(x => x.VaultId)
            .ToHashSetAsync(cancellationToken);

        foreach (var policy in policies.Where(x => affectedRoleIds.Contains(x.RoleId) && x.Policies.Count > 0))
        {
            var assigned = isActive && currentRoleIds.Contains(policy.RoleId);
            var missingMembership = assigned && policy.Policies.Any(x =>
                newlyEffectiveVaultIds.Contains(x.VaultId) && !memberships.Contains(x.VaultId));
            var existingMembershipToReconcile = !assigned && policy.Policies.Any(x =>
                noLongerEffectiveVaultIds.Contains(x.VaultId) && memberships.Contains(x.VaultId));
            await SupersedeCurrentOperationAsync(policy, now, cancellationToken);
            var operation = RoleVaultAccessOperation.Create(
                organizationId,
                guidProvider.Generate(),
                policy.RoleId,
                missingMembership ? 1 : 0,
                existingMembershipToReconcile ? 1 : 0,
                !missingMembership && !existingMembershipToReconcile ? 1 : 0,
                Guid.Empty,
                now);
            domainWriteContext.Add(operation);
            policy.Reconcile(operation.Id, Guid.Empty, now);
        }
    }

    internal async Task ReconcileRoleDeletionAsync(
        Guid organizationId,
        Guid roleId,
        Instant now,
        CancellationToken cancellationToken)
    {
        var policy = await domainWriteContext.RoleVaultAccessPolicySets
            .Include(x => x.Policies)
            .SingleOrDefaultAsync(
                x => x.OrganizationId == organizationId && x.RoleId == roleId,
                cancellationToken);
        if (policy is null)
        {
            return;
        }

        await SupersedeCurrentOperationAsync(policy, now, cancellationToken);
        if (policy.Policies.Count == 0)
        {
            return;
        }

        var selectedVaultIds = policy.Policies.Select(x => x.VaultId).ToArray();
        var affectedMembers = await domainWriteContext.OrganizationMemberRoleSets
            .Where(x => x.OrganizationId == organizationId
                        && x.IsActive
                        && x.RoleIds.Contains(roleId))
            .CountAsync(roleSet => domainWriteContext.VaultMembers.Any(member =>
                    member.OrganizationId == organizationId
                    && member.UserId == roleSet.UserId
                    && selectedVaultIds.Contains(member.VaultId)),
                cancellationToken);
        var operation = RoleVaultAccessOperation.Create(
            organizationId,
            guidProvider.Generate(),
            roleId,
            0,
            affectedMembers,
            0,
            Guid.Empty,
            now);
        domainWriteContext.Add(operation);
        policy.Replace([], operation.Id, Guid.Empty, now);
    }

    internal async Task<bool> ReconcileVaultDeletionAsync(
        Guid organizationId,
        Guid vaultId,
        Guid changedBy,
        Instant now,
        CancellationToken cancellationToken)
    {
        var policies = await domainWriteContext.RoleVaultAccessPolicySets
            .IgnoreQueryFilters()
            .Include(x => x.Policies)
            .Where(x => x.OrganizationId == organizationId
                        && x.Policies.Any(policy => policy.VaultId == vaultId))
            .OrderBy(x => x.RoleId)
            .Take(MaximumPoliciesPerVaultDeletion + 1)
            .ToListAsync(cancellationToken);
        if (policies.Count > MaximumPoliciesPerVaultDeletion)
        {
            return false;
        }

        var currentOperationIds = policies
            .Where(x => x.LastOperationId != null)
            .Select(x => x.LastOperationId!.Value)
            .ToArray();
        var currentOperations = await domainWriteContext.RoleVaultAccessOperations
            .Where(x => x.OrganizationId == organizationId && currentOperationIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        foreach (var policy in policies)
        {
            if (policy.LastOperationId is { } currentOperationId
                && currentOperations.GetValueOrDefault(currentOperationId) is { } currentOperation)
            {
                currentOperation.Supersede(now);
            }

            var operation = RoleVaultAccessOperation.Create(
                organizationId,
                guidProvider.Generate(),
                policy.RoleId,
                0,
                0,
                0,
                changedBy,
                now);
            operation.Supersede(now);
            domainWriteContext.Add(operation);
            policy.Replace(
                policy.Policies.Where(x => x.VaultId != vaultId).Select(x => x.VaultId).ToArray(),
                operation.Id,
                changedBy,
                now);
        }

        return true;
    }

    private async Task SupersedeCurrentOperationAsync(
        RoleVaultAccessPolicySet policy,
        Instant now,
        CancellationToken cancellationToken)
    {
        if (policy.LastOperationId is not { } operationId)
        {
            return;
        }

        var operation = await domainWriteContext.RoleVaultAccessOperations.SingleOrDefaultAsync(
            x => x.OrganizationId == policy.OrganizationId && x.Id == operationId,
            cancellationToken);
        operation?.Supersede(now);
    }
}
