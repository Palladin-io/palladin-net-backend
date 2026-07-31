using NodaTime;
using Palladin.Core.Events;
using Palladin.Core.Types.Exceptions;
using Palladin.Module.Vault.Contracts.Commands;
using Palladin.Module.Vault.Contracts.Events;

namespace Palladin.Module.Vault.Domain;

internal enum VaultPrincipalType
{
    OrganizationMember = 1,
    Agent = 2,
}

internal enum VaultPrincipalDeprovisioningStatus
{
    Pending = 1,
    WaitingForRotation = 2,
    BlockedLastMember = 3,
    Completed = 4,
}

internal sealed class VaultPrincipalDeprovisioning : EventEntityBase
{
    public Guid OrganizationId { get; private set; }
    public Guid Id { get; private set; }
    internal VaultPrincipalType PrincipalType { get; private set; }
    public Guid PrincipalId { get; private set; }
    public Guid RequestedBy { get; private set; }
    public Instant RequestedAt { get; private set; }
    internal VaultPrincipalDeprovisioningStatus Status { get; private set; }
    public Guid? CurrentVaultId { get; private set; }
    public Guid? CurrentRotationId { get; private set; }
    public uint CompletedVaultCount { get; private set; }
    public Instant UpdatedAt { get; private set; }
    public Instant? CompletedAt { get; private set; }

    private VaultPrincipalDeprovisioning() { }

    internal static VaultPrincipalDeprovisioning Create(
        Guid id,
        Guid organizationId,
        VaultPrincipalType principalType,
        Guid principalId,
        Guid requestedBy,
        Instant requestedAt)
    {
        if (id == Guid.Empty || organizationId == Guid.Empty || principalId == Guid.Empty || requestedBy == Guid.Empty)
        {
            throw new DomainException("Vault principal deprovisioning identifiers must not be empty.");
        }

        return new VaultPrincipalDeprovisioning
        {
            OrganizationId = organizationId,
            Id = id,
            PrincipalType = principalType,
            PrincipalId = principalId,
            RequestedBy = requestedBy,
            RequestedAt = requestedAt,
            Status = VaultPrincipalDeprovisioningStatus.Pending,
            UpdatedAt = requestedAt,
        };
    }

    internal void WaitForRotation(Guid vaultId, Guid rotationId, Instant now)
    {
        if (Status == VaultPrincipalDeprovisioningStatus.Completed)
        {
            throw new DomainException("Completed deprovisioning cannot start another Vault rotation.");
        }

        CurrentVaultId = vaultId;
        CurrentRotationId = rotationId;
        Status = VaultPrincipalDeprovisioningStatus.WaitingForRotation;
        UpdatedAt = now;
    }

    internal void BlockOnLastMember(Guid vaultId, Instant now)
    {
        if (PrincipalType != VaultPrincipalType.OrganizationMember)
        {
            throw new DomainException("Only Member removal can be blocked by the last-Member invariant.");
        }

        CurrentVaultId = vaultId;
        CurrentRotationId = null;
        Status = VaultPrincipalDeprovisioningStatus.BlockedLastMember;
        UpdatedAt = now;
    }

    internal void CompleteCurrentVault(Guid vaultId, Guid rotationId, Instant now)
    {
        if (Status != VaultPrincipalDeprovisioningStatus.WaitingForRotation
            || CurrentVaultId != vaultId
            || CurrentRotationId != rotationId)
        {
            throw new DomainException("Vault rotation does not match the current deprovisioning step.");
        }

        CompletedVaultCount = checked(CompletedVaultCount + 1);
        CurrentVaultId = null;
        CurrentRotationId = null;
        Status = VaultPrincipalDeprovisioningStatus.Pending;
        UpdatedAt = now;
    }

    internal void Complete(Instant now)
    {
        if (Status != VaultPrincipalDeprovisioningStatus.Pending)
        {
            throw new DomainException("Vault principal deprovisioning still has unfinished work.");
        }

        Status = VaultPrincipalDeprovisioningStatus.Completed;
        CompletedAt = now;
        UpdatedAt = now;
        QueueCompletionEvent(now);
    }

    internal void ResumeCompletion()
    {
        if (Status != VaultPrincipalDeprovisioningStatus.Completed || CompletedAt is null)
        {
            throw new DomainException("Only completed deprovisioning can resume completion delivery.");
        }

        QueueCompletionEvent(CompletedAt.Value);
    }

    private void QueueCompletionEvent(Instant completedAt)
    {
        if (PrincipalType == VaultPrincipalType.OrganizationMember)
        {
            AddOrReplaceEvent(new OrganizationMemberRemovalCompletedEvent(
                Id, OrganizationId, PrincipalId, completedAt, completedAt));
        }
        else
        {
            AddOrReplaceEvent(new AgentDeactivationCompletedEvent(
                Id, OrganizationId, PrincipalId, completedAt, completedAt));
        }
    }
}
