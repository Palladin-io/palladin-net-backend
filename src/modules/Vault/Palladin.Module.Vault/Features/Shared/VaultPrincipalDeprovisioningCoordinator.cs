using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Guid;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Persistence;

namespace Palladin.Module.Vault.Features;

internal sealed class VaultPrincipalDeprovisioningCoordinator(
    VaultDomainWriteContext domainWriteContext,
    IGuidProvider guidProvider)
{
    private const int GrantPageSize = 100;

    internal async Task StartAsync(
        Guid requestId,
        Guid organizationId,
        VaultPrincipalType principalType,
        Guid principalId,
        Guid requestedBy,
        Instant requestedAt,
        CancellationToken cancellationToken)
    {
        var lifecycle = await domainWriteContext.VaultOrganizationLifecycles
            .SingleOrDefaultAsync(x => x.OrganizationId == organizationId, cancellationToken);
        if (lifecycle is null)
        {
            lifecycle = VaultOrganizationLifecycle.Create(organizationId);
            domainWriteContext.Add(lifecycle);
        }
        else
        {
            lifecycle.FenceMutation();
        }

        var operation = await domainWriteContext.VaultPrincipalDeprovisionings
            .SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.Id == requestId, cancellationToken);
        if (operation is not null)
        {
            if (operation.Status == VaultPrincipalDeprovisioningStatus.Completed)
            {
                operation.ResumeCompletion();
                await domainWriteContext.CommitAsync(cancellationToken);
            }
            else if (operation.PrincipalType == VaultPrincipalType.Agent)
            {
                domainWriteContext.Clear();
                await CompleteAgentAsync(organizationId, requestId, requestedAt, cancellationToken);
            }

            return;
        }

        operation = VaultPrincipalDeprovisioning.Create(
            requestId, organizationId, principalType, principalId, requestedBy, requestedAt);
        domainWriteContext.Add(operation);
        if (principalType == VaultPrincipalType.Agent)
        {
            await domainWriteContext.CommitAsync(cancellationToken);
            domainWriteContext.Clear();
            await CompleteAgentAsync(organizationId, requestId, requestedAt, cancellationToken);
            return;
        }

        var anotherOperationIsRunning = await domainWriteContext.VaultPrincipalDeprovisionings
            .AnyAsync(x => x.OrganizationId == organizationId
                           && x.Status == VaultPrincipalDeprovisioningStatus.WaitingForRotation,
                cancellationToken);
        if (!anotherOperationIsRunning)
        {
            await AdvanceAsync(operation, requestedAt, cancellationToken);
        }

        await domainWriteContext.CommitAsync(cancellationToken);
    }

    // A redelivery after the completion was saved but not published must publish it again.
    internal async Task ResumeAgentAsync(
        Guid organizationId,
        Guid operationId,
        Instant now,
        CancellationToken cancellationToken)
    {
        var operation = await domainWriteContext.VaultPrincipalDeprovisionings.SingleOrDefaultAsync(
            x => x.OrganizationId == organizationId
                 && x.Id == operationId
                 && x.PrincipalType == VaultPrincipalType.Agent,
            cancellationToken);
        if (operation is null)
        {
            return;
        }

        if (operation.Status == VaultPrincipalDeprovisioningStatus.Completed)
        {
            operation.ResumeCompletion();
            await domainWriteContext.CommitAsync(cancellationToken);
            return;
        }

        domainWriteContext.Clear();
        await CompleteAgentAsync(organizationId, operationId, now, cancellationToken);
    }

    // Agent access is removed without waiting for a client. Each affected Vault keeps a durable rotation
    // requirement, persisted before its Agent envelopes are deleted so an interruption cannot lose it.
    private async Task CompleteAgentAsync(
        Guid organizationId,
        Guid operationId,
        Instant now,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var operation = await domainWriteContext.VaultPrincipalDeprovisionings.SingleAsync(
                x => x.OrganizationId == organizationId && x.Id == operationId, cancellationToken);
            if (operation.Status == VaultPrincipalDeprovisioningStatus.Completed)
            {
                return;
            }

            if (operation.Status == VaultPrincipalDeprovisioningStatus.WaitingForRotation)
            {
                await ReleaseWaitingAgentAsync(operation, now, cancellationToken);
                continue;
            }

            var vaultId = await FindNextAffectedVaultIdAsync(operation, cancellationToken);
            if (vaultId is null)
            {
                await StartQueuedMemberIfIdleAsync(organizationId, now, cancellationToken);
                operation.Complete(now);
                await domainWriteContext.CommitAsync(cancellationToken);
                return;
            }

            await RequireAgentRotationAsync(operation, vaultId.Value, now, cancellationToken);
            await DeleteAgentGrantEnvelopesAsync(
                organizationId, vaultId.Value, operation.PrincipalId, cancellationToken);

            operation = await domainWriteContext.VaultPrincipalDeprovisionings.SingleAsync(
                x => x.OrganizationId == organizationId && x.Id == operationId, cancellationToken);
            var vault = await domainWriteContext.Vaults
                .Include(x => x.AgentVaultDiscoveryEnvelopes
                    .Where(envelope => envelope.AgentId == operation.PrincipalId))
                .SingleAsync(x => x.OrganizationId == organizationId && x.Id == vaultId.Value, cancellationToken);
            vault.RemoveAgentDiscoveryPage(new HashSet<Guid> { operation.PrincipalId });
            vault.FenceAccessMutation(operation.RequestedBy, now);
            operation.RecordAgentVault(now);
            await domainWriteContext.CommitAsync(cancellationToken);
            domainWriteContext.Clear();
        }
    }

    private async Task ReleaseWaitingAgentAsync(
        VaultPrincipalDeprovisioning operation,
        Instant now,
        CancellationToken cancellationToken)
    {
        var organizationId = operation.OrganizationId;
        var rotation = await domainWriteContext.VaultKeyRotations.SingleOrDefaultAsync(
            x => x.OrganizationId == organizationId
                 && x.Id == operation.CurrentRotationId
                 && x.Status != VaultKeyRotationStatus.Committed,
            cancellationToken);
        rotation?.DetachAgentDeprovisioning(operation.Id);
        operation.ReleaseRotation(now);
        await domainWriteContext.CommitAsync(cancellationToken);
        domainWriteContext.Clear();
    }

    // Committed together with the Agent completion, so an interrupted hand-off is retried with it.
    private async Task StartQueuedMemberIfIdleAsync(
        Guid organizationId,
        Instant now,
        CancellationToken cancellationToken)
    {
        if (!await domainWriteContext.VaultPrincipalDeprovisionings.AnyAsync(
                x => x.OrganizationId == organizationId
                     && x.Status == VaultPrincipalDeprovisioningStatus.WaitingForRotation,
                cancellationToken))
        {
            await StartNextPendingAsync(organizationId, now, cancellationToken);
        }
    }

    private async Task RequireAgentRotationAsync(
        VaultPrincipalDeprovisioning operation,
        Guid vaultId,
        Instant now,
        CancellationToken cancellationToken)
    {
        // Rotation recipients are the Active replicas, so the Agent must stop being one in the same commit
        // that exposes the requirement; otherwise a fast rotation could seal the new keys to it.
        var agent = await domainWriteContext.Agents.SingleOrDefaultAsync(
            x => x.OrganizationId == operation.OrganizationId && x.Id == operation.PrincipalId,
            cancellationToken);
        agent?.BeginDeactivation(operation.RequestedAt);
        // The tracked Agent version must be read before its wrappers: a FULL grant committed in between
        // advances it, so this commit conflicts and retries instead of persisting too narrow a scope.
        var heldVaultKey = await domainWriteContext.Grants.OfType<FullGrant>().AnyAsync(
            x => x.OrganizationId == operation.OrganizationId
                 && x.VaultId == vaultId
                 && x.AgentId == operation.PrincipalId
                 && x.AgentWrappedVaultKey != null,
            cancellationToken);
        var scope = heldVaultKey
            ? VaultKeyRotationScope.VaultKey
              | VaultKeyRotationScope.Vdk
              | VaultKeyRotationScope.AgentMessage
              | VaultKeyRotationScope.ManifestSigning
            : VaultKeyRotationScope.Vdk;
        var vault = await domainWriteContext.Vaults.SingleAsync(
            x => x.OrganizationId == operation.OrganizationId && x.Id == vaultId, cancellationToken);
        var rotation = await domainWriteContext.VaultKeyRotations
            .Include(x => x.Reasons)
            .SingleOrDefaultAsync(
                x => x.OrganizationId == operation.OrganizationId
                     && x.VaultId == vaultId
                     && x.Status != VaultKeyRotationStatus.Committed,
                cancellationToken);
        if (rotation is null)
        {
            rotation = VaultKeyRotation.Create(
                guidProvider.Generate(),
                vault,
                VaultKeyRotationCause.AgentDeactivationRequested,
                scope,
                operation.RequestedBy,
                now);
            domainWriteContext.Add(rotation);
        }

        rotation.RequireForDeactivatedAgent(operation.PrincipalId, agent?.Name, scope, operation.RequestedAt);
        vault.FenceAccessMutation(operation.RequestedBy, now);
        await domainWriteContext.CommitAsync(cancellationToken);
        domainWriteContext.Clear();
    }

    private async Task DeleteAgentGrantEnvelopesAsync(
        Guid organizationId,
        Guid vaultId,
        Guid agentId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var grants = await domainWriteContext.Grants
                .Include(x => x.AgentWrappedVaultKey)
                .Include(x => x.ScriptExecutionPackage)
                .Include(x => x.GrantEntryScopes).ThenInclude(x => x.Envelope)
                .Where(x => x.OrganizationId == organizationId && x.VaultId == vaultId && x.AgentId == agentId)
                .Where(x => x.AgentWrappedVaultKey != null
                            || x.ScriptExecutionPackage != null
                            || x.GrantEntryScopes.Any(scope => scope.Envelope != null))
                .OrderBy(x => x.Id)
                .Take(GrantPageSize)
                .ToListAsync(cancellationToken);
            if (grants.Count == 0)
            {
                return;
            }

            foreach (var grant in grants)
            {
                grant.DeleteAgentEnvelopes();
            }

            await domainWriteContext.CommitAsync(cancellationToken);
            domainWriteContext.Clear();
        }
    }

    internal async Task AdvanceAsync(
        VaultPrincipalDeprovisioning operation,
        Instant now,
        CancellationToken cancellationToken)
    {
        var vaultId = await FindNextAffectedVaultIdAsync(operation, cancellationToken);
        if (vaultId is null)
        {
            operation.Complete(now);
            return;
        }

        if (operation.PrincipalType == VaultPrincipalType.OrganizationMember)
        {
            var memberCount = await domainWriteContext.VaultMembers.CountAsync(
                x => x.OrganizationId == operation.OrganizationId && x.VaultId == vaultId.Value,
                cancellationToken);
            if (memberCount <= 1)
            {
                operation.BlockOnLastMember(vaultId.Value, now);
                return;
            }
        }

        var vault = await domainWriteContext.Vaults.SingleAsync(
            x => x.OrganizationId == operation.OrganizationId && x.Id == vaultId.Value,
            cancellationToken);
        var rotation = await domainWriteContext.VaultKeyRotations
            .Include(x => x.Reasons)
            .SingleOrDefaultAsync(
                x => x.OrganizationId == operation.OrganizationId
                     && x.VaultId == vaultId.Value
                     && x.Status != VaultKeyRotationStatus.Committed,
                cancellationToken);
        if (rotation is null)
        {
            rotation = VaultKeyRotation.Create(
                guidProvider.Generate(),
                vault,
                operation.PrincipalType == VaultPrincipalType.OrganizationMember
                    ? VaultKeyRotationCause.MemberRemovalRequested
                    : VaultKeyRotationCause.AgentDeactivationRequested,
                operation.PrincipalType == VaultPrincipalType.OrganizationMember
                    ? VaultKeyRotationScope.VaultKey
                      | VaultKeyRotationScope.Vdk
                      | VaultKeyRotationScope.AgentMessage
                      | VaultKeyRotationScope.ManifestSigning
                    : VaultKeyRotationScope.Vdk,
                operation.RequestedBy,
                now);
            domainWriteContext.Add(rotation);
        }
        else if (rotation.DeprovisioningId is null && rotation.Status != VaultKeyRotationStatus.PendingClient)
        {
            return;
        }

        rotation.AttachDeprovisioning(operation);
        operation.WaitForRotation(vaultId.Value, rotation.Id, now);
        vault.FenceAccessMutation(operation.RequestedBy, now);
    }

    internal async Task StartNextPendingAsync(
        Guid organizationId,
        Instant now,
        CancellationToken cancellationToken,
        Guid? excludedOperationId = null)
    {
        var next = await domainWriteContext.VaultPrincipalDeprovisionings
            .Where(x => x.OrganizationId == organizationId
                        && x.PrincipalType == VaultPrincipalType.OrganizationMember
                        && x.Status == VaultPrincipalDeprovisioningStatus.Pending
                        && (excludedOperationId == null || x.Id != excludedOperationId))
            .OrderBy(x => x.RequestedAt)
            .ThenBy(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (next is not null)
        {
            await AdvanceAsync(next, now, cancellationToken);
        }
    }

    private Task<Guid?> FindNextAffectedVaultIdAsync(
        VaultPrincipalDeprovisioning operation,
        CancellationToken cancellationToken)
    {
        if (operation.PrincipalType == VaultPrincipalType.OrganizationMember)
        {
            return domainWriteContext.VaultMembers.AsNoTracking()
                .Where(x => x.OrganizationId == operation.OrganizationId
                            && x.UserId == operation.PrincipalId)
                .OrderBy(x => x.VaultId)
                .Select(x => (Guid?)x.VaultId)
                .FirstOrDefaultAsync(cancellationToken);
        }

        return domainWriteContext.Vaults.AsNoTracking()
            .Where(x => x.OrganizationId == operation.OrganizationId)
            .Where(x => domainWriteContext.AgentVaultDiscoveryEnvelopes.Any(envelope =>
                            envelope.OrganizationId == operation.OrganizationId
                            && envelope.VaultId == x.Id
                            && envelope.AgentId == operation.PrincipalId)
                        || domainWriteContext.Grants.Any(grant =>
                            grant.OrganizationId == operation.OrganizationId
                            && grant.VaultId == x.Id
                            && grant.AgentId == operation.PrincipalId
                            && (grant.AgentWrappedVaultKey != null
                                || grant.GrantEntryScopes.Any(scope => scope.Envelope != null))))
            .OrderBy(x => x.Id)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
