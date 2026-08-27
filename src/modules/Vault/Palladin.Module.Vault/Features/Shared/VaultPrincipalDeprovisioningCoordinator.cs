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

            return;
        }

        operation = VaultPrincipalDeprovisioning.Create(
            requestId, organizationId, principalType, principalId, requestedBy, requestedAt);
        domainWriteContext.Add(operation);
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
        var rotation = await domainWriteContext.VaultKeyRotations.SingleOrDefaultAsync(
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
