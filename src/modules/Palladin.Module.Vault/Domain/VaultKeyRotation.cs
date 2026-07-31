using NodaTime;
using Palladin.Core.Types.Exceptions;

namespace Palladin.Module.Vault.Domain;

[Flags]
internal enum VaultKeyRotationScope
{
    None = 0,
    VaultKey = 1,
    Vdk = 2,
    AgentMessage = 4,
    ManifestSigning = 8,
}

internal enum VaultKeyRotationCause
{
    MemberRemovalRequested = 1,
    AgentDeactivationRequested = 2,
    SuspectedCompromise = 3,
    ProtocolMigration = 4,
    ManualSecurityRotation = 5,
}

internal enum VaultKeyRotationStatus
{
    PendingClient = 1,
    Preparing = 2,
    ReadyToCommit = 3,
    Committed = 4,
    FailedRetryable = 5,
}

internal sealed class VaultKeyRotation
{
    public Guid OrganizationId { get; private set; }
    public Guid VaultId { get; private set; }
    public Guid Id { get; private set; }
    internal VaultKeyRotationCause Cause { get; private set; }
    internal VaultKeyRotationScope Scope { get; private set; }
    internal VaultKeyRotationStatus Status { get; private set; }
    internal MemberKeyGeneration BaseMemberKeyGeneration { get; private set; }
    internal VaultKeyEpoch BaseKeyEpoch { get; private set; } = null!;
    internal MemberSequence BaseMemberSequence { get; private set; }
    internal DiscoverySequence BaseDiscoverySequence { get; private set; }
    internal MemberKeyGeneration TargetMemberKeyGeneration { get; private set; }
    internal VaultKeyEpoch TargetKeyEpoch { get; private set; } = null!;
    internal Guid TriggeredBy { get; private set; }
    internal Instant TriggeredAt { get; private set; }
    internal Guid? LeaseOwnerId { get; private set; }
    internal Guid? FencingToken { get; private set; }
    internal Instant? LeaseExpiresAt { get; private set; }
    internal ulong LeaseRevision { get; private set; }
    internal Instant? CommittedAt { get; private set; }
    internal string? LastFailureCode { get; private set; }
    public Guid? DeprovisioningId { get; private set; }
    public Guid? ExcludedMemberId { get; private set; }
    public Guid? ExcludedAgentId { get; private set; }
    internal ICollection<VaultKeyRotationPreparedItem> PreparedItems { get; private set; } = [];
    internal VaultKeyMaterialKind[] RequiredKeyMaterialKinds => Scope.HasFlag(VaultKeyRotationScope.VaultKey)
        ? Enum.GetValues<VaultKeyMaterialKind>()
        : Enum.GetValues<VaultKeyMaterialKind>().Where(kind => kind switch
        {
            VaultKeyMaterialKind.DiscoveryKey => Scope.HasFlag(VaultKeyRotationScope.Vdk),
            VaultKeyMaterialKind.AgentMessagePrivateKey => Scope.HasFlag(VaultKeyRotationScope.AgentMessage),
            VaultKeyMaterialKind.ManifestSigningPrivateKey => Scope.HasFlag(VaultKeyRotationScope.ManifestSigning),
            _ => false,
        }).ToArray();

    private VaultKeyRotation() { }

    internal static VaultKeyRotation Create(
        Guid id,
        Vault vault,
        VaultKeyRotationCause cause,
        VaultKeyRotationScope scope,
        Guid triggeredBy,
        Instant triggeredAt)
    {
        if (id == Guid.Empty || triggeredBy == Guid.Empty)
        {
            throw new DomainException("Vault key rotation identifiers must not be empty.");
        }

        ValidateScope(scope);

        return new VaultKeyRotation
        {
            OrganizationId = vault.OrganizationId,
            VaultId = vault.Id,
            Id = id,
            Cause = cause,
            Scope = scope,
            Status = VaultKeyRotationStatus.PendingClient,
            BaseMemberKeyGeneration = vault.MemberKeyGeneration,
            BaseKeyEpoch = vault.CurrentKeyEpoch,
            BaseMemberSequence = vault.MemberSequence,
            BaseDiscoverySequence = vault.DiscoverySequence,
            TargetMemberKeyGeneration = scope.HasFlag(VaultKeyRotationScope.VaultKey)
                ? Next(vault.MemberKeyGeneration)
                : vault.MemberKeyGeneration,
            TargetKeyEpoch = new VaultKeyEpoch(
                scope.HasFlag(VaultKeyRotationScope.VaultKey)
                    ? Next(vault.CurrentVaultKeyVersion)
                    : vault.CurrentVaultKeyVersion,
                scope.HasFlag(VaultKeyRotationScope.Vdk)
                    ? Next(vault.CurrentVdkVersion)
                    : vault.CurrentVdkVersion,
                scope.HasFlag(VaultKeyRotationScope.AgentMessage)
                    ? Next(vault.CurrentAgentMessageKeyVersion)
                    : vault.CurrentAgentMessageKeyVersion,
                scope.HasFlag(VaultKeyRotationScope.ManifestSigning)
                    ? Next(vault.CurrentManifestSigningKeyVersion)
                    : vault.CurrentManifestSigningKeyVersion),
            TriggeredBy = triggeredBy,
            TriggeredAt = triggeredAt,
        };
    }

    internal Guid Claim(Guid memberId, Guid fencingToken, Instant now, Duration leaseDuration)
    {
        if (memberId == Guid.Empty || fencingToken == Guid.Empty || leaseDuration <= Duration.Zero)
        {
            throw new DomainException("Vault key rotation lease is invalid.");
        }

        if (Status is VaultKeyRotationStatus.Committed or VaultKeyRotationStatus.ReadyToCommit)
        {
            throw new DomainException("Vault key rotation cannot be claimed in its current state.");
        }

        if (LeaseExpiresAt > now && LeaseOwnerId != memberId)
        {
            throw new VaultKeyRotationLeaseConflictException();
        }

        LeaseRevision = checked(LeaseRevision + 1);
        LeaseOwnerId = memberId;
        FencingToken = fencingToken;
        LeaseExpiresAt = now + leaseDuration;
        Status = VaultKeyRotationStatus.Preparing;
        LastFailureCode = null;
        return fencingToken;
    }

    internal void AttachDeprovisioning(VaultPrincipalDeprovisioning deprovisioning)
    {
        if (Status != VaultKeyRotationStatus.PendingClient
            || deprovisioning.OrganizationId != OrganizationId
            || deprovisioning.CurrentVaultId is not null)
        {
            throw new DomainException("Deprovisioning cannot be attached to this Vault rotation.");
        }

        if (DeprovisioningId is not null && DeprovisioningId != deprovisioning.Id)
        {
            throw new DomainException("Vault rotation already serves another deprovisioning operation.");
        }

        DeprovisioningId = deprovisioning.Id;
        Cause = deprovisioning.PrincipalType == VaultPrincipalType.OrganizationMember
            ? VaultKeyRotationCause.MemberRemovalRequested
            : VaultKeyRotationCause.AgentDeactivationRequested;
        var requiredScope = deprovisioning.PrincipalType == VaultPrincipalType.OrganizationMember
            ? VaultKeyRotationScope.VaultKey
              | VaultKeyRotationScope.Vdk
              | VaultKeyRotationScope.AgentMessage
              | VaultKeyRotationScope.ManifestSigning
            : VaultKeyRotationScope.Vdk;
        Scope |= requiredScope;
        ExcludedMemberId = deprovisioning.PrincipalType == VaultPrincipalType.OrganizationMember
            ? deprovisioning.PrincipalId
            : null;
        ExcludedAgentId = deprovisioning.PrincipalType == VaultPrincipalType.Agent
            ? deprovisioning.PrincipalId
            : null;
        TargetMemberKeyGeneration = Scope.HasFlag(VaultKeyRotationScope.VaultKey)
            ? Next(BaseMemberKeyGeneration)
            : BaseMemberKeyGeneration;
        TargetKeyEpoch = new VaultKeyEpoch(
            Scope.HasFlag(VaultKeyRotationScope.VaultKey)
                ? Next(BaseKeyEpoch.VaultKeyVersion)
                : BaseKeyEpoch.VaultKeyVersion,
            Scope.HasFlag(VaultKeyRotationScope.Vdk)
                ? Next(BaseKeyEpoch.VdkVersion)
                : BaseKeyEpoch.VdkVersion,
            Scope.HasFlag(VaultKeyRotationScope.AgentMessage)
                ? Next(BaseKeyEpoch.AgentMessageKeyVersion)
                : BaseKeyEpoch.AgentMessageKeyVersion,
            Scope.HasFlag(VaultKeyRotationScope.ManifestSigning)
                ? Next(BaseKeyEpoch.ManifestSigningKeyVersion)
                : BaseKeyEpoch.ManifestSigningKeyVersion);
    }

    internal void AssertLease(Guid memberId, Guid fencingToken, Instant now)
    {
        if (Status != VaultKeyRotationStatus.Preparing
            || LeaseOwnerId != memberId
            || FencingToken != fencingToken
            || LeaseExpiresAt <= now)
        {
            throw new VaultKeyRotationFenceException();
        }
    }

    internal bool Prepare(VaultKeyRotationPreparedItem item, Guid memberId, Guid fencingToken, Instant now)
    {
        AssertLease(memberId, fencingToken, now);
        if (item.OrganizationId != OrganizationId || item.VaultId != VaultId || item.RotationId != Id)
        {
            throw new DomainException("Prepared item does not belong to this Vault rotation.");
        }

        var existing = PreparedItems.SingleOrDefault(x =>
            x.Kind == item.Kind && x.SubjectId == item.SubjectId && x.SubjectVersion == item.SubjectVersion);
        if (existing is null)
        {
            PreparedItems.Add(item);
            return true;
        }

        if (existing.HasSameContent(item))
        {
            return false;
        }

        existing.ReplaceWith(item);
        return true;
    }

    internal void MarkReady(Guid memberId, Guid fencingToken, Instant now)
    {
        AssertLease(memberId, fencingToken, now);
        Status = VaultKeyRotationStatus.ReadyToCommit;
    }

    internal void MarkRetryableFailure(string failureCode)
    {
        if (string.IsNullOrWhiteSpace(failureCode) || failureCode.Length > 64)
        {
            throw new DomainException("Vault key rotation failure code is invalid.");
        }

        if (Status == VaultKeyRotationStatus.Committed)
        {
            throw new DomainException("Committed Vault key rotation cannot fail.");
        }

        Status = VaultKeyRotationStatus.FailedRetryable;
        LeaseOwnerId = null;
        FencingToken = null;
        LeaseExpiresAt = null;
        LastFailureCode = failureCode;
    }

    internal void MarkCommitted(Instant committedAt)
    {
        if (Status != VaultKeyRotationStatus.ReadyToCommit)
        {
            throw new DomainException("Vault key rotation must be ready before commit.");
        }

        Status = VaultKeyRotationStatus.Committed;
        LeaseOwnerId = null;
        FencingToken = null;
        LeaseExpiresAt = null;
        CommittedAt = committedAt;
        LastFailureCode = null;
    }

    private static void ValidateScope(VaultKeyRotationScope scope)
    {
        const VaultKeyRotationScope supported = VaultKeyRotationScope.VaultKey
                                                | VaultKeyRotationScope.Vdk
                                                | VaultKeyRotationScope.AgentMessage
                                                | VaultKeyRotationScope.ManifestSigning;
        if (scope == VaultKeyRotationScope.None || (scope & ~supported) != 0)
        {
            throw new DomainException("Vault key rotation scope is invalid.");
        }
    }

    private static MemberKeyGeneration Next(MemberKeyGeneration current) =>
        new(checked(current.Value + 1));

    private static VaultKeyVersion Next(VaultKeyVersion current) =>
        new(checked(current.Value + 1));

    private static VdkVersion Next(VdkVersion current) =>
        new(checked(current.Value + 1));

    private static AgentMessageKeyVersion Next(AgentMessageKeyVersion current) =>
        new(checked(current.Value + 1));

    private static ManifestSigningKeyVersion Next(ManifestSigningKeyVersion current) =>
        new(checked(current.Value + 1));
}

internal sealed class VaultKeyRotationLeaseConflictException()
    : ConflictException("Vault key rotation is already leased by another Member.");

internal sealed class VaultKeyRotationFenceException()
    : ConflictException("Vault key rotation lease is missing, stale or expired.");
