using NodaTime;
using Palladin.Core.Events;
using Palladin.Core.Types;
using Palladin.Core.Types.Exceptions;
using Palladin.Module.Vault.Contracts.Events;

namespace Palladin.Module.Vault.Domain;

internal sealed class VaultEntry : EventEntityBase
{
    public Guid OrganizationId { get; private set; }
    public Guid VaultId { get; private set; }
    public Guid Id { get; private set; }
    public EntryState State { get; private set; }
    internal EntryRevision CurrentRevision { get; private set; }
    internal MemberIndexRevision MemberIndexRevision { get; private set; }
    internal AgentDiscoveryRevision? AgentDiscoveryRevision { get; private set; }
    internal AgentDiscoveryRevisionWatermark AgentDiscoveryRevisionHighWatermark { get; private set; }
    internal EntryKeyVersion CurrentKeyVersion { get; private set; }
    public Instant CreatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }
    public Instant UpdatedAt { get; private set; }
    public Guid UpdatedBy { get; private set; }
    public Instant? ArchivedAt { get; private set; }
    public Guid? ArchivedBy { get; private set; }
    public Instant? DeletedAt { get; private set; }
    public Guid? DeletedBy { get; private set; }

    internal ushort MemberIndexProtocolVersion { get; private set; }
    internal string MemberIndexCryptoSuiteId { get; private set; } = string.Empty;
    internal MemberKeyGeneration MemberIndexMemberKeyGeneration { get; private set; }
    internal byte[] MemberIndexEncodedSuitePayload { get; private set; } = [];

    internal ushort? AgentDiscoveryProtocolVersion { get; private set; }
    internal string? AgentDiscoveryCryptoSuiteId { get; private set; }
    internal VdkVersion? AgentDiscoveryVdkVersion { get; private set; }
    internal MemberKeyGeneration? AgentDiscoveryMemberKeyGeneration { get; private set; }
    internal byte[]? AgentDiscoveryEncodedSuitePayload { get; private set; }

    internal ICollection<VaultEntryKey> Keys { get; private set; } = [];
    internal ICollection<VaultEntryVersion> Versions { get; private set; } = [];

    internal EntryScope Scope => new(OrganizationId, VaultId, Id);

    private VaultEntry() { }

    internal static VaultEntry Create(
        EntryScope scope,
        VaultEntryKey entryKey,
        VaultEntryVersion version,
        MemberIndexCiphertext memberIndex,
        AgentDiscoveryCiphertext? agentDiscovery,
        MemberKeyGeneration currentMemberKeyGeneration,
        VaultKeyVersion currentVaultKeyVersion,
        VdkVersion currentVdkVersion,
        Instant now,
        Guid createdBy,
        bool viaImport = false)
    {
        scope.Validate();
        ValidateCreate(
            scope,
            entryKey,
            version,
            memberIndex,
            agentDiscovery,
            currentMemberKeyGeneration,
            currentVaultKeyVersion,
            currentVdkVersion,
            createdBy);

        var entry = new VaultEntry
        {
            OrganizationId = scope.OrganizationId,
            VaultId = scope.VaultId,
            Id = scope.EntryId,
            State = EntryState.Active,
            CurrentRevision = version.Revision,
            MemberIndexRevision = memberIndex.Revision,
            AgentDiscoveryRevision = agentDiscovery?.Revision,
            AgentDiscoveryRevisionHighWatermark = new AgentDiscoveryRevisionWatermark(
                agentDiscovery?.Revision.Value ?? 0),
            CurrentKeyVersion = entryKey.KeyVersion,
            CreatedAt = now,
            CreatedBy = createdBy,
            UpdatedAt = now,
            UpdatedBy = createdBy,
            MemberIndexProtocolVersion = memberIndex.Header.ProtocolVersion,
            MemberIndexCryptoSuiteId = CryptoSuiteId.XChaCha20Poly1305V1,
            MemberIndexMemberKeyGeneration = memberIndex.Header.MemberKeyGeneration,
            MemberIndexEncodedSuitePayload = SuitePayload.Encode(memberIndex.Header.Nonce, memberIndex.Ciphertext),
        };

        entry.SetAgentDiscovery(agentDiscovery);
        entry.Keys.Add(entryKey);
        entry.Versions.Add(version);
        entry.EmitUpserted(createdBy, EntityChange.Created, now, viaImport);
        return entry;
    }

    internal bool IsExactCreateRetry(
        VaultEntryKey entryKey,
        MemberSecretCiphertext memberSecret,
        MemberIndexCiphertext memberIndex,
        AgentDiscoveryCiphertext? agentDiscovery) =>
        State == EntryState.Active
        && CurrentRevision.Value == 1
        && CurrentKeyVersion.Value == 1
        && Keys.SingleOrDefault(x => x.KeyVersion.Value == 1)?.HasSameContent(entryKey) == true
        && Versions.SingleOrDefault(x => x.Revision.Value == 1)?.GetMemberSecret().HasSameContent(memberSecret) == true
        && GetMemberIndex().HasSameContent(memberIndex)
        && AgentDiscoveryEquals(agentDiscovery);

    internal bool IsExactUpdateRetry(
        EntryRevision baseRevision,
        VaultEntryKey? newKey,
        MemberSecretCiphertext memberSecret,
        MemberIndexCiphertext? memberIndex,
        bool agentDiscoveryChanged,
        AgentDiscoveryCiphertext? agentDiscovery)
    {
        if (baseRevision.Value == ulong.MaxValue
            || CurrentRevision.Value != baseRevision.Value + 1
            || Versions.SingleOrDefault(x => x.Revision == CurrentRevision) is not { } currentVersion
            || Versions.SingleOrDefault(x => x.Revision == baseRevision) is not { } previousVersion
            || !currentVersion.GetMemberSecret().HasSameContent(memberSecret))
        {
            return false;
        }

        var entryKeyChanged = currentVersion.KeyVersion != previousVersion.KeyVersion;
        if ((newKey is not null) != entryKeyChanged
            || (newKey is not null
                && Keys.SingleOrDefault(x => x.KeyVersion == newKey.KeyVersion)?.HasSameContent(newKey) != true))
        {
            return false;
        }

        if ((memberIndex is not null) != currentVersion.MemberIndexChanged
            || (memberIndex is not null && !GetMemberIndex().HasSameContent(memberIndex)))
        {
            return false;
        }

        var expectedAgentDiscoveryChange = currentVersion.DiscoverySequence is not null;
        return agentDiscoveryChanged == expectedAgentDiscoveryChange
               && (!agentDiscoveryChanged || AgentDiscoveryEquals(agentDiscovery));
    }

    internal bool IsExactLifecycleRetry(
        EntryRevision baseRevision,
        EntryState expectedState,
        VaultEntryKey? newKey,
        MemberSecretCiphertext memberSecret,
        MemberIndexCiphertext? memberIndex,
        AgentDiscoveryCiphertext? agentDiscovery)
    {
        if (State != expectedState
            || baseRevision.Value == ulong.MaxValue
            || CurrentRevision.Value != baseRevision.Value + 1
            || Versions.SingleOrDefault(x => x.Revision == CurrentRevision) is not { } currentVersion
            || Versions.SingleOrDefault(x => x.Revision == baseRevision) is not { } previousVersion
            || !currentVersion.GetMemberSecret().HasSameContent(memberSecret))
        {
            return false;
        }

        var keyChanged = currentVersion.KeyVersion != previousVersion.KeyVersion;
        if ((newKey is not null) != keyChanged
            || (newKey is not null
                && Keys.SingleOrDefault(x => x.KeyVersion == newKey.KeyVersion)?.HasSameContent(newKey) != true)
            || ((memberIndex is not null) != currentVersion.MemberIndexChanged)
            || (memberIndex is not null && !GetMemberIndex().HasSameContent(memberIndex)))
        {
            return false;
        }

        var discoveryChanged = currentVersion.DiscoverySequence is not null;
        return expectedState == EntryState.Active
            ? discoveryChanged == (agentDiscovery is not null) && AgentDiscoveryEquals(agentDiscovery)
            : agentDiscovery is null && GetAgentDiscovery() is null;
    }

    internal void CommitKeyRotation(
        IReadOnlyCollection<VaultEntryKey> rewrappedKeys,
        AgentDiscoveryCiphertext? discovery,
        bool rewrapEntryKeys,
        bool rotateDiscovery,
        MemberKeyGeneration targetMemberKeyGeneration,
        VaultKeyVersion targetVaultKeyVersion,
        VdkVersion targetVdkVersion,
        Guid committedBy,
        Instant committedAt)
    {
        if (rewrapEntryKeys && rewrappedKeys.Count != Keys.Count)
        {
            throw new DomainException("Vault rotation must re-wrap every retained Entry key.");
        }

        foreach (var current in rewrapEntryKeys ? Keys : [])
        {
            var replacement = rewrappedKeys.SingleOrDefault(x => x.KeyVersion == current.KeyVersion)
                ?? throw new DomainException("Vault rotation is missing a retained Entry key wrapper.");
            if (replacement.MemberKeyGeneration != targetMemberKeyGeneration
                || replacement.WrappingKeyVersion != targetVaultKeyVersion)
            {
                throw new DomainException("Rotated Entry key wrapper does not target the committed Vault generation.");
            }

            current.ApplyRotationRewrap(replacement);
        }

        if (rotateDiscovery && (AgentDiscoveryRevision is not null) != (discovery is not null))
        {
            throw new DomainException("Vault rotation Discovery coverage does not match the current discoverable Entry set.");
        }

        if (rotateDiscovery && discovery is not null)
        {
            if (discovery.Scope != Scope
                || discovery.VdkVersion != targetVdkVersion
                || discovery.Header.MemberKeyGeneration != targetMemberKeyGeneration
                || discovery.Revision != AgentDiscoveryRevision!.Value)
            {
                throw new DomainException("Rotated Agent Discovery projection does not preserve the current structural revision.");
            }

            SetAgentDiscovery(discovery);
            AgentDiscoveryRevisionHighWatermark = new AgentDiscoveryRevisionWatermark(discovery.Revision.Value);
        }

        UpdatedBy = committedBy;
        UpdatedAt = committedAt;
    }

    internal void Update(
        EntryRevision baseRevision,
        MemberKeyGeneration currentMemberKeyGeneration,
        VaultKeyVersion currentVaultKeyVersion,
        VdkVersion currentVdkVersion,
        VaultEntryKey? newKey,
        MemberSecretCiphertext memberSecret,
        MemberIndexCiphertext? memberIndex,
        bool agentDiscoveryChanged,
        AgentDiscoveryCiphertext? agentDiscovery,
        AllocatedVaultSequences sequences,
        Instant now,
        Guid updatedBy)
    {
        if (State != EntryState.Active)
        {
            throw new InvalidEntryStateTransitionException("update", State);
        }

        if (baseRevision != CurrentRevision)
        {
            throw new EntryRevisionConflictException();
        }

        if (CurrentRevision.Value == ulong.MaxValue
            || memberSecret.Revision.Value != CurrentRevision.Value + 1
            || memberSecret.Operation != EntryOperation.Updated)
        {
            throw new DomainException("Entry update must create exactly the next canonical revision.");
        }

        ValidateScope(memberSecret.Scope);
        var currentKey = Keys.Single(x => x.KeyVersion == CurrentKeyVersion);
        var targetKey = ValidateAndSelectTargetKey(
            newKey,
            currentKey,
            currentMemberKeyGeneration,
            currentVaultKeyVersion);
        ValidateMemberSecret(memberSecret, targetKey);
        ValidateProjectionMutation(
            memberIndex,
            agentDiscoveryChanged,
            agentDiscovery,
            newKey,
            currentKey,
            targetKey,
            currentMemberKeyGeneration,
            currentVdkVersion);

        var version = VaultEntryVersion.Create(
            memberSecret,
            sequences,
            memberIndex is not null,
            now,
            ActorType.Member,
            updatedBy);
        if (newKey is not null)
        {
            Keys.Add(newKey);
        }

        Versions.Add(version);
        CurrentRevision = version.Revision;
        CurrentKeyVersion = targetKey.KeyVersion;
        UpdatedAt = now;
        UpdatedBy = updatedBy;

        if (memberIndex is not null)
        {
            SetMemberIndex(memberIndex);
        }

        if (agentDiscoveryChanged)
        {
            SetAgentDiscovery(agentDiscovery);
        }

        EmitUpserted(updatedBy, EntityChange.Updated, now);
    }

    internal void Archive(
        EntryRevision baseRevision,
        MemberKeyGeneration currentMemberKeyGeneration,
        VaultKeyVersion currentVaultKeyVersion,
        VaultEntryKey? newKey,
        MemberSecretCiphertext memberSecret,
        MemberIndexCiphertext? memberIndex,
        AllocatedVaultSequences sequences,
        Instant now,
        Guid archivedBy)
    {
        if (State != EntryState.Active)
        {
            throw new InvalidEntryStateTransitionException("archive", State);
        }

        ApplyLifecycleTransition(
            baseRevision,
            currentMemberKeyGeneration,
            currentVaultKeyVersion,
            newKey,
            memberSecret,
            memberIndex,
            EntryOperation.Archived,
            sequences,
            now,
            archivedBy);

        State = EntryState.Archived;
        ArchivedAt = now;
        ArchivedBy = archivedBy;
    }

    internal void Delete(
        EntryRevision baseRevision,
        MemberKeyGeneration currentMemberKeyGeneration,
        VaultKeyVersion currentVaultKeyVersion,
        VaultEntryKey? newKey,
        MemberSecretCiphertext memberSecret,
        MemberIndexCiphertext? memberIndex,
        AllocatedVaultSequences sequences,
        Instant now,
        Guid deletedBy)
    {
        if (State is not (EntryState.Active or EntryState.Archived))
        {
            throw new InvalidEntryStateTransitionException("delete", State);
        }

        ApplyLifecycleTransition(
            baseRevision,
            currentMemberKeyGeneration,
            currentVaultKeyVersion,
            newKey,
            memberSecret,
            memberIndex,
            EntryOperation.Deleted,
            sequences,
            now,
            deletedBy);

        State = EntryState.Deleted;
        ArchivedAt = null;
        ArchivedBy = null;
        DeletedAt = now;
        DeletedBy = deletedBy;
    }

    internal void Restore(
        EntryRevision baseRevision,
        MemberKeyGeneration currentMemberKeyGeneration,
        VaultKeyVersion currentVaultKeyVersion,
        VdkVersion currentVdkVersion,
        VaultEntryKey? newKey,
        MemberSecretCiphertext memberSecret,
        MemberIndexCiphertext? memberIndex,
        AgentDiscoveryCiphertext? agentDiscovery,
        AllocatedVaultSequences sequences,
        Duration recentlyDeletedRetention,
        Instant now,
        Guid restoredBy)
    {
        if (State is not (EntryState.Archived or EntryState.Deleted))
        {
            throw new InvalidEntryStateTransitionException("restore", State);
        }

        if (State == EntryState.Deleted
            && (DeletedAt is null || DeletedAt.Value + recentlyDeletedRetention <= now))
        {
            throw new InvalidEntryStateTransitionException("restore outside its retention window", State);
        }

        ValidateLifecycleRevision(baseRevision, memberSecret, EntryOperation.Restored);
        var currentKey = Keys.Single(x => x.KeyVersion == CurrentKeyVersion);
        var targetKey = ValidateAndSelectTargetKey(
            newKey,
            currentKey,
            currentMemberKeyGeneration,
            currentVaultKeyVersion);
        ValidateMemberSecret(memberSecret, targetKey);
        ValidateProjectionMutation(
            memberIndex,
            agentDiscovery is not null,
            agentDiscovery,
            newKey,
            currentKey,
            targetKey,
            currentMemberKeyGeneration,
            currentVdkVersion);
        AppendVersion(
            newKey,
            memberSecret,
            memberIndex,
            agentDiscovery is not null,
            agentDiscovery,
            sequences,
            now,
            restoredBy,
            targetKey);

        State = EntryState.Active;
        ArchivedAt = null;
        ArchivedBy = null;
        DeletedAt = null;
        DeletedBy = null;
    }

    internal RemovedEntryHistory PurgeHistory(
        EntryRevision? removeBeforeRevision,
        Instant? removeChangedBefore)
    {
        var removable = Versions
            .Where(x => x.Revision != CurrentRevision
                        && ((removeBeforeRevision is not null
                             && x.Revision.Value < removeBeforeRevision.Value.Value)
                            || (removeChangedBefore is not null
                                && x.ChangedAt < removeChangedBefore.Value)))
            .ToList();
        if (removable.Count == 0)
        {
            return RemovedEntryHistory.Empty;
        }

        var memberFloor = removable.Max(x => x.MemberSequence.Value);
        var discoveryFloor = removable
            .Where(x => x.DiscoverySequence is not null)
            .Select(x => x.DiscoverySequence!.Value.Value)
            .DefaultIfEmpty(0UL)
            .Max();
        foreach (var version in removable)
        {
            Versions.Remove(version);
        }

        var retainedKeyVersions = Versions.Select(x => x.KeyVersion).Append(CurrentKeyVersion).ToHashSet();
        var removableKeys = Keys.Where(x => !retainedKeyVersions.Contains(x.KeyVersion)).ToList();
        foreach (var key in removableKeys)
        {
            Keys.Remove(key);
        }

        return new RemovedEntryHistory(
            removable.Count,
            memberFloor,
            discoveryFloor,
            removable,
            removableKeys);
    }

    private void ApplyLifecycleTransition(
        EntryRevision baseRevision,
        MemberKeyGeneration currentMemberKeyGeneration,
        VaultKeyVersion currentVaultKeyVersion,
        VaultEntryKey? newKey,
        MemberSecretCiphertext memberSecret,
        MemberIndexCiphertext? memberIndex,
        EntryOperation operation,
        AllocatedVaultSequences sequences,
        Instant now,
        Guid changedBy)
    {
        ValidateLifecycleRevision(baseRevision, memberSecret, operation);
        var currentKey = Keys.Single(x => x.KeyVersion == CurrentKeyVersion);
        var targetKey = ValidateAndSelectTargetKey(
            newKey,
            currentKey,
            currentMemberKeyGeneration,
            currentVaultKeyVersion);
        ValidateMemberSecret(memberSecret, targetKey);
        var disablesDiscovery = GetAgentDiscovery() is not null;
        ValidateProjectionMutation(
            memberIndex,
            disablesDiscovery,
            null,
            newKey,
            currentKey,
            targetKey,
            currentMemberKeyGeneration,
            default);
        AppendVersion(
            newKey,
            memberSecret,
            memberIndex,
            disablesDiscovery,
            null,
            sequences,
            now,
            changedBy,
            targetKey);
    }

    private void ValidateLifecycleRevision(
        EntryRevision baseRevision,
        MemberSecretCiphertext memberSecret,
        EntryOperation operation)
    {
        if (baseRevision != CurrentRevision)
        {
            throw new EntryRevisionConflictException();
        }

        if (CurrentRevision.Value == ulong.MaxValue
            || memberSecret.Revision.Value != CurrentRevision.Value + 1
            || memberSecret.Operation != operation)
        {
            throw new DomainException("Entry lifecycle transition must create exactly the next canonical revision.");
        }

        ValidateScope(memberSecret.Scope);
    }

    private void AppendVersion(
        VaultEntryKey? newKey,
        MemberSecretCiphertext memberSecret,
        MemberIndexCiphertext? memberIndex,
        bool agentDiscoveryChanged,
        AgentDiscoveryCiphertext? agentDiscovery,
        AllocatedVaultSequences sequences,
        Instant now,
        Guid changedBy,
        VaultEntryKey targetKey)
    {
        var version = VaultEntryVersion.Create(
            memberSecret,
            sequences,
            memberIndex is not null,
            now,
            ActorType.Member,
            changedBy);
        if (newKey is not null)
        {
            Keys.Add(newKey);
        }

        Versions.Add(version);
        CurrentRevision = version.Revision;
        CurrentKeyVersion = targetKey.KeyVersion;
        UpdatedAt = now;
        UpdatedBy = changedBy;
        if (memberIndex is not null)
        {
            SetMemberIndex(memberIndex);
        }

        if (agentDiscoveryChanged)
        {
            SetAgentDiscovery(agentDiscovery);
        }

        EmitUpserted(changedBy, EntityChange.Updated, now);
    }

    internal MemberIndexCiphertext GetMemberIndex()
    {
        var header = EntryEnvelopeHeader.Create(
            MemberIndexProtocolVersion,
            VaultProtocol.AlgorithmSuite,
            VaultProtocol.EntryResourceKind,
            VaultProtocol.MemberIndexProjectionKind,
            MemberIndexRevision.Value,
            CurrentKeyVersion.Value,
            MemberIndexMemberKeyGeneration,
            SuitePayload.Nonce(MemberIndexEncodedSuitePayload),
            VaultProtocol.MemberIndexProjectionKind);

        return Domain.MemberIndexCiphertext.Create(Scope, MemberIndexRevision, header,
            SuitePayload.Ciphertext(MemberIndexEncodedSuitePayload));
    }

    internal AgentDiscoveryCiphertext? GetAgentDiscovery()
    {
        if (AgentDiscoveryRevision is null)
        {
            return null;
        }

        var header = EntryEnvelopeHeader.Create(
            AgentDiscoveryProtocolVersion!.Value,
            VaultProtocol.AlgorithmSuite,
            VaultProtocol.EntryResourceKind,
            VaultProtocol.AgentDiscoveryProjectionKind,
            AgentDiscoveryRevision.Value.Value,
            AgentDiscoveryVdkVersion!.Value.Value,
            AgentDiscoveryMemberKeyGeneration!.Value,
            SuitePayload.Nonce(AgentDiscoveryEncodedSuitePayload!),
            VaultProtocol.AgentDiscoveryProjectionKind);

        return Domain.AgentDiscoveryCiphertext.Create(
            Scope,
            AgentDiscoveryRevision.Value,
            AgentDiscoveryVdkVersion.Value,
            header,
            SuitePayload.Ciphertext(AgentDiscoveryEncodedSuitePayload!));
    }

    private void SetMemberIndex(MemberIndexCiphertext memberIndex)
    {
        MemberIndexRevision = memberIndex.Revision;
        MemberIndexProtocolVersion = memberIndex.Header.ProtocolVersion;
        MemberIndexCryptoSuiteId = CryptoSuiteId.XChaCha20Poly1305V1;
        MemberIndexMemberKeyGeneration = memberIndex.Header.MemberKeyGeneration;
        MemberIndexEncodedSuitePayload = SuitePayload.Encode(memberIndex.Header.Nonce, memberIndex.Ciphertext);
    }

    private void SetAgentDiscovery(AgentDiscoveryCiphertext? agentDiscovery)
    {
        if (agentDiscovery is not null)
        {
            AgentDiscoveryRevisionHighWatermark = new AgentDiscoveryRevisionWatermark(
                agentDiscovery.Revision.Value);
        }

        AgentDiscoveryRevision = agentDiscovery?.Revision;
        AgentDiscoveryProtocolVersion = agentDiscovery?.Header.ProtocolVersion;
        AgentDiscoveryCryptoSuiteId = agentDiscovery is null ? null : CryptoSuiteId.XChaCha20Poly1305V1;
        AgentDiscoveryVdkVersion = agentDiscovery?.VdkVersion;
        AgentDiscoveryMemberKeyGeneration = agentDiscovery?.Header.MemberKeyGeneration;
        AgentDiscoveryEncodedSuitePayload = agentDiscovery is null
            ? null
            : SuitePayload.Encode(agentDiscovery.Header.Nonce, agentDiscovery.Ciphertext);
    }

    private bool AgentDiscoveryEquals(AgentDiscoveryCiphertext? candidate)
    {
        var current = GetAgentDiscovery();
        return current is null ? candidate is null : candidate is not null && current.HasSameContent(candidate);
    }

    private VaultEntryKey ValidateAndSelectTargetKey(
        VaultEntryKey? newKey,
        VaultEntryKey currentKey,
        MemberKeyGeneration currentMemberKeyGeneration,
        VaultKeyVersion currentVaultKeyVersion)
    {
        if (currentKey.MemberKeyGeneration != currentMemberKeyGeneration && newKey is null)
        {
            throw new DomainException("A fresh Entry key version is required after the Vault Member key generation changes.");
        }

        if (newKey is null)
        {
            return currentKey;
        }

        ValidateScope(newKey.Scope);
        if (CurrentKeyVersion.Value == uint.MaxValue
            || newKey.KeyVersion.Value != CurrentKeyVersion.Value + 1
            || newKey.WrapperRevision.Value != 1
            || newKey.MemberKeyGeneration != currentMemberKeyGeneration
            || newKey.WrappingKeyVersion != currentVaultKeyVersion)
        {
            throw new DomainException("A new Entry key must start at wrapper revision 1, use the next key version and current Vault key generation.");
        }

        return newKey;
    }

    private void ValidateProjectionMutation(
        MemberIndexCiphertext? memberIndex,
        bool agentDiscoveryChanged,
        AgentDiscoveryCiphertext? agentDiscovery,
        VaultEntryKey? newKey,
        VaultEntryKey previousKey,
        VaultEntryKey targetKey,
        MemberKeyGeneration currentMemberKeyGeneration,
        VdkVersion currentVdkVersion)
    {
        if (newKey is not null && memberIndex is null)
        {
            throw new DomainException("A new Entry key requires a replacement Member index projection.");
        }

        if (previousKey.MemberKeyGeneration != currentMemberKeyGeneration
            && GetAgentDiscovery() is not null
            && !agentDiscoveryChanged)
        {
            throw new DomainException("A Member generation change requires replacement Agent Discovery ciphertext.");
        }

        if (memberIndex is not null)
        {
            ValidateScope(memberIndex.Scope);
            if (MemberIndexRevision.Value == ulong.MaxValue
                || memberIndex.Revision.Value != MemberIndexRevision.Value + 1
                || memberIndex.Header.KeyVersion != targetKey.KeyVersion.Value
                || memberIndex.Header.MemberKeyGeneration != targetKey.MemberKeyGeneration)
            {
                throw new DomainException("Changed Member index must advance its own revision and bind the selected Entry key.");
            }
        }

        if (!agentDiscoveryChanged && agentDiscovery is not null)
        {
            throw new DomainException("Unchanged Agent Discovery cannot carry replacement ciphertext.");
        }

        if (agentDiscoveryChanged && agentDiscovery is null && GetAgentDiscovery() is null)
        {
            throw new DomainException("Agent Discovery is already disabled and cannot emit another disable transition.");
        }

        if (agentDiscovery is not null)
        {
            ValidateScope(agentDiscovery.Scope);
            if (AgentDiscoveryRevisionHighWatermark.Value == ulong.MaxValue
                || agentDiscovery.Revision.Value != AgentDiscoveryRevisionHighWatermark.Value + 1
                || agentDiscovery.VdkVersion != currentVdkVersion
                || agentDiscovery.Header.MemberKeyGeneration != targetKey.MemberKeyGeneration)
            {
                throw new DomainException("Changed Agent Discovery must advance its own revision and bind the current Vault keys.");
            }
        }
    }

    private static void ValidateCreate(
        EntryScope scope,
        VaultEntryKey entryKey,
        VaultEntryVersion version,
        MemberIndexCiphertext memberIndex,
        AgentDiscoveryCiphertext? agentDiscovery,
        MemberKeyGeneration currentMemberKeyGeneration,
        VaultKeyVersion currentVaultKeyVersion,
        VdkVersion currentVdkVersion,
        Guid createdBy)
    {
        if (entryKey.Scope != scope || version.Scope != scope || memberIndex.Scope != scope
            || (agentDiscovery is not null && agentDiscovery.Scope != scope))
        {
            throw new DomainException("Entry creation payload contains a substituted scope.");
        }

        if (entryKey.KeyVersion.Value != 1
            || entryKey.WrapperRevision.Value != 1
            || version.Revision.Value != 1
            || version.Operation != EntryOperation.Created
            || version.ChangedByType != ActorType.Member
            || version.ChangedById != createdBy
            || version.KeyVersion != entryKey.KeyVersion
            || memberIndex.Revision.Value != 1
            || memberIndex.Header.KeyVersion != entryKey.KeyVersion.Value
            || memberIndex.Header.MemberKeyGeneration != entryKey.MemberKeyGeneration
            || entryKey.MemberKeyGeneration != currentMemberKeyGeneration
            || entryKey.WrappingKeyVersion != currentVaultKeyVersion
            || (agentDiscovery is not null
                && (agentDiscovery.Revision.Value != 1
                    || agentDiscovery.VdkVersion != currentVdkVersion
                    || agentDiscovery.Header.MemberKeyGeneration != currentMemberKeyGeneration)))
        {
            throw new DomainException("A new Entry must be a complete revision and key version 1 transition.");
        }

        ValidateMemberSecret(version.GetMemberSecret(), entryKey);
    }

    private static void ValidateMemberSecret(MemberSecretCiphertext memberSecret, VaultEntryKey key)
    {
        if (memberSecret.Scope != key.Scope
            || memberSecret.Header.KeyVersion != key.KeyVersion.Value
            || memberSecret.Header.MemberKeyGeneration != key.MemberKeyGeneration)
        {
            throw new DomainException("Member secret is not bound to the selected Entry key.");
        }
    }

    private void ValidateScope(EntryScope scope)
    {
        if (scope != Scope)
        {
            throw new DomainException("Entry mutation payload contains a substituted scope.");
        }
    }

    private void EmitUpserted(Guid actorId, EntityChange change, Instant updatedAt, bool viaImport = false) =>
        AddOrReplaceEvent(new EntryUpsertedEvent(
            OrganizationId,
            VaultId,
            Id,
            actorId,
            change,
            CurrentRevision.Value,
            updatedAt,
            viaImport));
}

internal sealed class EntryRevisionConflictException()
    : ConflictException("Entry base revision is stale.");

internal sealed class InvalidEntryStateTransitionException(string operation, EntryState state)
    : ConflictException($"Entry cannot {operation} while it is {state}.");

internal sealed record RemovedEntryHistory(
    int RemovedVersions,
    ulong MemberSequenceFloor,
    ulong DiscoverySequenceFloor,
    IReadOnlyList<VaultEntryVersion> Versions,
    IReadOnlyList<VaultEntryKey> Keys)
{
    internal static RemovedEntryHistory Empty { get; } = new(0, 0, 0, [], []);
}
