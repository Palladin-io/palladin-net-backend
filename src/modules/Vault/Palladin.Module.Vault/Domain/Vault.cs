using NodaTime;
using Palladin.Core.Events;
using Palladin.Core.Types;
using Palladin.Core.Types.Exceptions;
using Palladin.Module.Vault.Contracts.Events;

namespace Palladin.Module.Vault.Domain;

internal sealed class Vault : EventEntityBase
{
    public Guid OrganizationId { get; private set; }
    public Guid Id { get; private set; }
    public bool IsDefault { get; private set; }
    public Guid CreatedBy { get; private set; }
    public Instant CreatedAt { get; private set; }
    public Guid UpdatedBy { get; private set; }
    public Instant UpdatedAt { get; private set; }
    internal ulong MutationVersion { get; private set; }
    internal bool IsDeleting { get; private set; }
    internal Guid? DeletionRequestedBy { get; private set; }
    internal string? DeletionRequestedByName { get; private set; }
    internal Instant? DeletionRequestedAt { get; private set; }

    internal ushort ProtocolVersion { get; private set; }
    internal MetadataRevision MetadataRevision { get; private set; }
    internal MemberSequence MemberSequence { get; private set; }
    internal DiscoverySequence DiscoverySequence { get; private set; }
    internal MemberSequence MinRetainedMemberSequence { get; private set; }
    internal DiscoverySequence MinRetainedDiscoverySequence { get; private set; }
    internal MemberKeyGeneration MemberKeyGeneration { get; private set; }
    internal VaultKeyVersion CurrentVaultKeyVersion { get; private set; }
    internal VdkVersion CurrentVdkVersion { get; private set; }
    internal AgentMessageKeyVersion CurrentAgentMessageKeyVersion { get; private set; }
    internal ManifestSigningKeyVersion CurrentManifestSigningKeyVersion { get; private set; }
    internal string MemberVaultMetadataCryptoSuiteId { get; private set; } = string.Empty;
    internal VaultKeyVersion MemberVaultMetadataKeyVersion { get; private set; }
    internal byte[] MemberVaultMetadataEncodedSuitePayload { get; private set; } = [];
    internal byte[] ManifestSigningPublicKey { get; private set; } = [];
    internal byte[] ManifestSigningKeyFingerprint { get; private set; } = [];
    internal byte[] AgentMessagePublicKey { get; private set; } = [];
    internal byte[] AgentMessageKeyFingerprint { get; private set; } = [];

    public ICollection<VaultMember> VaultMembers { get; private set; } = [];
    internal ICollection<VaultMemberKeyEnvelope> VaultMemberKeyEnvelopes { get; private set; } = [];
    internal ICollection<AgentVaultDiscoveryEnvelope> AgentVaultDiscoveryEnvelopes { get; private set; } = [];
    internal ICollection<VaultKeyMaterialEnvelope> KeyMaterialEnvelopes { get; private set; } = [];

    internal VaultScope Scope => new(OrganizationId, Id);
    internal VaultKeyEpoch CurrentKeyEpoch => new(
        CurrentVaultKeyVersion,
        CurrentVdkVersion,
        CurrentAgentMessageKeyVersion,
        CurrentManifestSigningKeyVersion);

    private Vault() { }

    internal static Vault Create(
        Guid id,
        Guid organizationId,
        Guid createdBy,
        string actorName,
        MemberVaultMetadataCiphertext metadata,
        MemberKeyGeneration memberKeyGeneration,
        VaultKeyEpoch currentKeyEpoch,
        MemberWrappedVaultKey creatorWrappedVaultKey,
        IReadOnlyCollection<VaultKeyMaterialEnvelope> keyMaterial,
        ValidatedVaultPublicKey agentMessagePublicKey,
        ValidatedVaultPublicKey manifestSigningPublicKey,
        Instant now) =>
        Create(
            id,
            organizationId,
            createdBy,
            actorName,
            metadata,
            memberKeyGeneration,
            currentKeyEpoch,
            creatorWrappedVaultKey,
            keyMaterial,
            agentMessagePublicKey,
            manifestSigningPublicKey,
            false,
            now);

    internal static Vault CreateDefault(
        Guid id,
        Guid organizationId,
        Guid createdBy,
        string actorName,
        MemberVaultMetadataCiphertext metadata,
        MemberKeyGeneration memberKeyGeneration,
        VaultKeyEpoch currentKeyEpoch,
        MemberWrappedVaultKey creatorWrappedVaultKey,
        IReadOnlyCollection<VaultKeyMaterialEnvelope> keyMaterial,
        ValidatedVaultPublicKey agentMessagePublicKey,
        ValidatedVaultPublicKey manifestSigningPublicKey,
        Instant now) =>
        Create(
            id,
            organizationId,
            createdBy,
            actorName,
            metadata,
            memberKeyGeneration,
            currentKeyEpoch,
            creatorWrappedVaultKey,
            keyMaterial,
            agentMessagePublicKey,
            manifestSigningPublicKey,
            true,
            now);

    private static Vault Create(
        Guid id,
        Guid organizationId,
        Guid createdBy,
        string actorName,
        MemberVaultMetadataCiphertext metadata,
        MemberKeyGeneration memberKeyGeneration,
        VaultKeyEpoch currentKeyEpoch,
        MemberWrappedVaultKey creatorWrappedVaultKey,
        IReadOnlyCollection<VaultKeyMaterialEnvelope> keyMaterial,
        ValidatedVaultPublicKey agentMessagePublicKey,
        ValidatedVaultPublicKey manifestSigningPublicKey,
        bool isDefault,
        Instant now)
    {
        var scope = new VaultScope(organizationId, id);
        scope.Validate();
        ValidateInitialState(metadata, memberKeyGeneration, currentKeyEpoch);
        ValidateMetadata(scope, metadata, memberKeyGeneration, currentKeyEpoch.VaultKeyVersion);
        ValidateMemberKey(
            scope,
            createdBy,
            creatorWrappedVaultKey,
            memberKeyGeneration,
            currentKeyEpoch.VaultKeyVersion);
        ValidateKeyMaterial(scope, keyMaterial, memberKeyGeneration, currentKeyEpoch, expectedRevision: 1);
        if (agentMessagePublicKey.Kind != VaultPublicKeyKind.AgentMessageX25519
            || agentMessagePublicKey.Version != currentKeyEpoch.AgentMessageKeyVersion.Value
            || manifestSigningPublicKey.Kind != VaultPublicKeyKind.ManifestSigningEd25519
            || manifestSigningPublicKey.Version != currentKeyEpoch.ManifestSigningKeyVersion.Value)
            throw new DomainException("Vault public-key versions must match the initial key epoch.");

        var vault = new Vault
        {
            OrganizationId = organizationId,
            Id = id,
            IsDefault = isDefault,
            CreatedBy = createdBy,
            CreatedAt = now,
            UpdatedBy = createdBy,
            UpdatedAt = now,
            MutationVersion = 1,
            ProtocolVersion = VaultProtocol.CurrentVersion,
            MetadataRevision = metadata.MetadataRevision,
            MemberSequence = new MemberSequence(0),
            DiscoverySequence = new DiscoverySequence(0),
            MinRetainedMemberSequence = new MemberSequence(0),
            MinRetainedDiscoverySequence = new DiscoverySequence(0),
            MemberKeyGeneration = memberKeyGeneration,
            CurrentVaultKeyVersion = currentKeyEpoch.VaultKeyVersion,
            CurrentVdkVersion = currentKeyEpoch.VdkVersion,
            CurrentAgentMessageKeyVersion = currentKeyEpoch.AgentMessageKeyVersion,
            CurrentManifestSigningKeyVersion = currentKeyEpoch.ManifestSigningKeyVersion,
            MemberVaultMetadataCryptoSuiteId = CryptoSuiteId.XChaCha20Poly1305V1,
            MemberVaultMetadataKeyVersion = metadata.Header.KeyVersion,
            MemberVaultMetadataEncodedSuitePayload = SuitePayload.Encode(metadata.Header.Nonce, metadata.Ciphertext),
            AgentMessagePublicKey = agentMessagePublicKey.PublicKey.ToArray(),
            AgentMessageKeyFingerprint = agentMessagePublicKey.Fingerprint.ToArray(),
            ManifestSigningPublicKey = manifestSigningPublicKey.PublicKey.ToArray(),
            ManifestSigningKeyFingerprint = manifestSigningPublicKey.Fingerprint.ToArray(),
        };

        vault.AddInitialMember(createdBy, creatorWrappedVaultKey, now);
        foreach (var envelope in keyMaterial)
        {
            vault.KeyMaterialEnvelopes.Add(envelope);
        }
        vault.EmitUpserted(createdBy, actorName, EntityChange.Created, now);
        vault.EmitSyncInvalidated(now);

        return vault;
    }

    internal MemberVaultMetadataCiphertext GetMemberVaultMetadata()
    {
        var header = MemberVaultMetadataHeader.Create(
            ProtocolVersion,
            VaultProtocol.AlgorithmSuite,
            VaultProtocol.VaultResourceKind,
            VaultProtocol.MemberVaultMetadataProjectionKind,
            MetadataRevision,
            MemberVaultMetadataKeyVersion,
            MemberKeyGeneration,
            SuitePayload.Nonce(MemberVaultMetadataEncodedSuitePayload));

        return Domain.MemberVaultMetadataCiphertext.Create(
            Scope,
            MetadataRevision,
            header,
            SuitePayload.Ciphertext(MemberVaultMetadataEncodedSuitePayload));
    }

    internal (VaultMember Member, VaultMemberKeyEnvelope KeyEnvelope) AddMember(
        Guid userId,
        MemberWrappedVaultKey wrappedVaultKey,
        Instant now)
    {
        if (IsDefault)
        {
            throw new DefaultVaultNotShareableException();
        }

        if (VaultMembers.Any(x => x.UserId == userId))
        {
            throw new DomainException("User is already a Vault Member.");
        }

        ValidateMemberKey(Scope, userId, wrappedVaultKey, MemberKeyGeneration, CurrentVaultKeyVersion);

        var added = AddMemberCore(userId, wrappedVaultKey, now);
        AdvanceMutationVersion();
        EmitSyncInvalidated(now);
        return added;
    }

    internal void ReplaceMetadata(
        Guid updatedBy,
        string actorName,
        MemberVaultMetadataCiphertext metadata,
        Instant now)
    {
        ValidateMetadata(Scope, metadata, MemberKeyGeneration, CurrentVaultKeyVersion);

        if (MetadataRevision.Value == ulong.MaxValue)
        {
            throw new DomainException("Member Vault metadata revision namespace is exhausted.");
        }

        var expectedRevision = MetadataRevision.Value + 1;
        if (metadata.MetadataRevision.Value != expectedRevision)
        {
            throw new DomainException("Member Vault metadata revision must equal the next server-owned revision.");
        }

        MetadataRevision = metadata.MetadataRevision;
        MemberVaultMetadataCryptoSuiteId = CryptoSuiteId.XChaCha20Poly1305V1;
        MemberVaultMetadataKeyVersion = metadata.Header.KeyVersion;
        MemberVaultMetadataEncodedSuitePayload = SuitePayload.Encode(metadata.Header.Nonce, metadata.Ciphertext);
        UpdatedBy = updatedBy;
        UpdatedAt = now;
        AdvanceMutationVersion();
        EmitUpserted(updatedBy, actorName, EntityChange.Updated, now);
        EmitSyncInvalidated(now);
    }

    internal AllocatedVaultSequences AllocateSequences(
        bool discoveryProjectionChanged,
        Guid updatedBy,
        Instant now)
    {
        MemberSequence = new MemberSequence(checked(MemberSequence.Value + 1));
        DiscoverySequence? allocatedDiscoverySequence = null;

        if (discoveryProjectionChanged)
        {
            DiscoverySequence = new DiscoverySequence(checked(DiscoverySequence.Value + 1));
            allocatedDiscoverySequence = DiscoverySequence;
        }

        UpdatedBy = updatedBy;
        UpdatedAt = now;
        AdvanceMutationVersion();
        EmitSyncInvalidated(now);

        return new AllocatedVaultSequences(MemberSequence, allocatedDiscoverySequence);
    }

    internal void AdvanceRetentionFloors(
        MemberSequence memberFloor,
        DiscoverySequence discoveryFloor,
        Guid updatedBy,
        Instant now)
    {
        if (memberFloor.Value < MinRetainedMemberSequence.Value
            || discoveryFloor.Value < MinRetainedDiscoverySequence.Value)
        {
            throw new DomainException("Vault retention floors cannot move backwards.");
        }

        if (memberFloor.Value > MemberSequence.Value || discoveryFloor.Value > DiscoverySequence.Value)
        {
            throw new DomainException("Vault retention floors cannot exceed the allocated sequence.");
        }

        MinRetainedMemberSequence = memberFloor;
        MinRetainedDiscoverySequence = discoveryFloor;
        UpdatedBy = updatedBy;
        UpdatedAt = now;
        AdvanceMutationVersion();
    }

    internal bool ProvisionAgentDiscovery(
        Agent agent,
        ValidatedAgentDiscoveryProvisioning provisioning,
        Guid provisionedBy,
        Instant now)
    {
        if (agent.OrganizationId != OrganizationId
            || agent.Id != provisioning.AgentId
            || agent.Status != AgentStatus.Active)
        {
            throw new DomainException("Agent Discovery can be provisioned only to an active Agent in the Vault organization.");
        }

        if (provisioning.Scope != Scope
            || provisioning.VdkVersion != CurrentVdkVersion
            || provisioning.Manifest.ManifestSigningKeyVersion != CurrentManifestSigningKeyVersion
            || provisioning.Manifest.AgentMessageKeyVersion != CurrentAgentMessageKeyVersion
            || provisioning.RecipientAgentKeyVersion.Value != agent.RecipientKeyVersion)
        {
            throw new DomainException("Agent Discovery provisioning does not target the current Vault and Agent key versions.");
        }

        BindAgentTrustAnchors(provisioning.Manifest);
        var existing = AgentVaultDiscoveryEnvelopes.SingleOrDefault(x => x.AgentId == agent.Id);
        var changed = existing is null
            ? AddAgentDiscoveryEnvelope(provisioning, provisionedBy, agent.AccessEpoch, now)
            : existing.Replace(provisioning, provisionedBy, agent.AccessEpoch, now);

        if (changed)
        {
            UpdatedBy = provisionedBy;
            UpdatedAt = now;
            AdvanceMutationVersion();
        }

        return changed;
    }

    internal bool RevokeAgentDiscovery(
        Guid agentId,
        Instant deactivatedAt,
        uint deactivatedAccessEpoch)
    {
        var existing = AgentVaultDiscoveryEnvelopes.SingleOrDefault(x => x.AgentId == agentId);
        var changed = existing is not null
                      && existing.RevokeForAcceptedAgentDeactivation(
                          deactivatedAt,
                          deactivatedAccessEpoch);
        if (changed)
        {
            AdvanceMutationVersion();
        }

        return changed;
    }

    internal bool DeleteAgentDiscovery(Guid agentId)
    {
        var existing = AgentVaultDiscoveryEnvelopes.SingleOrDefault(x => x.AgentId == agentId);
        var changed = existing is not null && AgentVaultDiscoveryEnvelopes.Remove(existing);
        if (changed)
        {
            AdvanceMutationVersion();
        }

        return changed;
    }

    internal void CommitKeyRotation(
        VaultKeyRotation rotation,
        MemberVaultMetadataCiphertext? metadata,
        IReadOnlyCollection<VaultKeyMaterialEnvelope> keyMaterial,
        ValidatedVaultManifest? agentManifest,
        ValidatedVaultPublicKey? agentMessagePublicKey,
        ValidatedVaultPublicKey? manifestSigningPublicKey,
        Guid committedBy,
        Instant committedAt)
    {
        var resetMemberSync = rotation.Scope.HasFlag(VaultKeyRotationScope.VaultKey);
        var postRotationMemberSequence = resetMemberSync
            ? new MemberSequence(checked(MemberSequence.Value + 1))
            : MemberSequence;
        if (rotation.OrganizationId != OrganizationId || rotation.VaultId != Id
            || rotation.BaseMemberKeyGeneration != MemberKeyGeneration
            || rotation.BaseKeyEpoch != CurrentKeyEpoch
            || (rotation.Scope.HasFlag(VaultKeyRotationScope.VaultKey)
                && (metadata is null
                    || metadata.Scope != Scope
                    || metadata.MetadataRevision.Value != checked(MetadataRevision.Value + 1)
                    || metadata.Header.MemberKeyGeneration != rotation.TargetMemberKeyGeneration
                    || metadata.Header.KeyVersion != rotation.TargetKeyEpoch.VaultKeyVersion))
            || (!rotation.Scope.HasFlag(VaultKeyRotationScope.VaultKey) && metadata is not null))
        {
            throw new DomainException("Vault rotation no longer matches the current Vault state.");
        }

        var requiredKeyMaterial = rotation.Scope.HasFlag(VaultKeyRotationScope.VaultKey)
            ? Enum.GetValues<VaultKeyMaterialKind>()
            : Enum.GetValues<VaultKeyMaterialKind>().Where(kind => kind switch
            {
                VaultKeyMaterialKind.DiscoveryKey => rotation.Scope.HasFlag(VaultKeyRotationScope.Vdk),
                VaultKeyMaterialKind.AgentMessagePrivateKey => rotation.Scope.HasFlag(VaultKeyRotationScope.AgentMessage),
                VaultKeyMaterialKind.ManifestSigningPrivateKey => rotation.Scope.HasFlag(VaultKeyRotationScope.ManifestSigning),
                _ => false,
            }).ToArray();
        if (keyMaterial.Count != requiredKeyMaterial.Length
            || keyMaterial.Select(x => x.Kind).Order().SequenceEqual(requiredKeyMaterial.Order()) is false)
        {
            throw new DomainException("Vault rotation does not contain the exact required encrypted key material.");
        }
        foreach (var replacement in keyMaterial)
        {
            var current = KeyMaterialEnvelopes.Single(x => x.Kind == replacement.Kind);
            var expectedVersion = replacement.Kind switch
            {
                VaultKeyMaterialKind.DiscoveryKey => rotation.TargetKeyEpoch.VdkVersion.Value,
                VaultKeyMaterialKind.AgentMessagePrivateKey => rotation.TargetKeyEpoch.AgentMessageKeyVersion.Value,
                VaultKeyMaterialKind.ManifestSigningPrivateKey => rotation.TargetKeyEpoch.ManifestSigningKeyVersion.Value,
                _ => throw new DomainException("Unknown Vault key material kind."),
            };
            if (replacement.KeyVersion != expectedVersion
                || replacement.MemberKeyGeneration != rotation.TargetMemberKeyGeneration
                || replacement.WrappingKeyVersion != rotation.TargetKeyEpoch.VaultKeyVersion)
            {
                throw new DomainException("Rotated Vault key material does not target the committed key epoch.");
            }
            current.ReplaceWith(replacement);
        }

        if ((rotation.Scope.HasFlag(VaultKeyRotationScope.AgentMessage) && agentMessagePublicKey is null)
            || (rotation.Scope.HasFlag(VaultKeyRotationScope.ManifestSigning) && manifestSigningPublicKey is null)
            || (agentManifest is not null
            && (agentManifest.Scope != Scope
                || agentManifest.VdkVersion != rotation.TargetKeyEpoch.VdkVersion
                || agentManifest.AgentMessageKeyVersion != rotation.TargetKeyEpoch.AgentMessageKeyVersion
                || agentManifest.ManifestSigningKeyVersion != rotation.TargetKeyEpoch.ManifestSigningKeyVersion)))
        {
            throw new DomainException("Rotated Agent Discovery envelope does not target the committed key epoch.");
        }

        if (agentManifest is not null
            && ((agentMessagePublicKey is not null
                 && !agentMessagePublicKey.PublicKey.AsSpan().SequenceEqual(agentManifest.VaultAgentMessagePublicKey))
                || (manifestSigningPublicKey is not null
                    && !manifestSigningPublicKey.PublicKey.AsSpan().SequenceEqual(agentManifest.VaultSigningPublicKey))))
        {
            throw new DomainException("Rotated Agent manifests do not match the committed public trust anchors.");
        }

        MemberKeyGeneration = rotation.TargetMemberKeyGeneration;
        CurrentVaultKeyVersion = rotation.TargetKeyEpoch.VaultKeyVersion;
        CurrentVdkVersion = rotation.TargetKeyEpoch.VdkVersion;
        CurrentAgentMessageKeyVersion = rotation.TargetKeyEpoch.AgentMessageKeyVersion;
        CurrentManifestSigningKeyVersion = rotation.TargetKeyEpoch.ManifestSigningKeyVersion;
        if (resetMemberSync)
        {
            MemberSequence = postRotationMemberSequence;
            MinRetainedMemberSequence = postRotationMemberSequence;
        }
        if (metadata is not null)
        {
            MetadataRevision = metadata.MetadataRevision;
            MemberVaultMetadataCryptoSuiteId = CryptoSuiteId.XChaCha20Poly1305V1;
            MemberVaultMetadataKeyVersion = metadata.Header.KeyVersion;
            MemberVaultMetadataEncodedSuitePayload = SuitePayload.Encode(metadata.Header.Nonce, metadata.Ciphertext);
        }

        if (agentMessagePublicKey is not null)
        {
            AgentMessagePublicKey = agentMessagePublicKey.PublicKey.ToArray();
            AgentMessageKeyFingerprint = agentMessagePublicKey.Fingerprint.ToArray();
        }
        if (manifestSigningPublicKey is not null)
        {
            ManifestSigningPublicKey = manifestSigningPublicKey.PublicKey.ToArray();
            ManifestSigningKeyFingerprint = manifestSigningPublicKey.Fingerprint.ToArray();
        }

        UpdatedBy = committedBy;
        UpdatedAt = committedAt;
        AdvanceMutationVersion();
        EmitSyncInvalidated(committedAt);
    }

    internal void AddRotatedMemberKeyPage(
        MemberKeyGeneration targetGeneration,
        VaultKeyVersion targetVaultKeyVersion,
        IReadOnlyCollection<MemberWrappedVaultKey> memberKeys)
    {
        foreach (var memberKey in memberKeys)
        {
            ValidateMemberKey(Scope, memberKey.MemberId, memberKey, targetGeneration, targetVaultKeyVersion);
            VaultMemberKeyEnvelopes.Add(VaultMemberKeyEnvelope.Create(memberKey));
        }
    }

    internal ValidatedVaultManifest? ApplyRotatedAgentDiscoveryPage(
        VaultKeyEpoch targetEpoch,
        IReadOnlyCollection<(ValidatedAgentDiscoveryProvisioning Provisioning, uint AccessEpoch)> targets,
        ValidatedVaultManifest? expectedManifest,
        Guid committedBy,
        Instant committedAt)
    {
        foreach (var (provisioning, accessEpoch) in targets)
        {
            if (accessEpoch == 0 || provisioning.Scope != Scope
                || provisioning.VdkVersion != targetEpoch.VdkVersion
                || provisioning.Manifest.AgentMessageKeyVersion != targetEpoch.AgentMessageKeyVersion
                || provisioning.Manifest.ManifestSigningKeyVersion != targetEpoch.ManifestSigningKeyVersion
                || expectedManifest is not null && !HasSameRotationTrustAnchors(expectedManifest, provisioning.Manifest))
            {
                throw new DomainException("Rotated Agent Discovery envelope does not target the committed key epoch.");
            }

            expectedManifest ??= provisioning.Manifest;
            var existing = AgentVaultDiscoveryEnvelopes.SingleOrDefault(x => x.AgentId == provisioning.AgentId);
            if (existing is null)
            {
                AgentVaultDiscoveryEnvelopes.Add(AgentVaultDiscoveryEnvelope.Create(
                    provisioning, committedBy, accessEpoch, committedAt));
            }
            else
            {
                existing.Replace(provisioning, committedBy, accessEpoch, committedAt);
            }
        }

        return expectedManifest;
    }

    internal void RemoveAgentDiscoveryPage(IReadOnlySet<Guid> agentIds)
    {
        foreach (var envelope in AgentVaultDiscoveryEnvelopes.Where(x => agentIds.Contains(x.AgentId)).ToArray())
        {
            AgentVaultDiscoveryEnvelopes.Remove(envelope);
        }
    }

    internal void RemoveMemberForCommittedRotation(Guid userId, Instant occurredAt)
    {
        foreach (var envelope in VaultMemberKeyEnvelopes.Where(x => x.MemberId == userId).ToArray())
        {
            VaultMemberKeyEnvelopes.Remove(envelope);
        }

        var member = VaultMembers.SingleOrDefault(x => x.UserId == userId);
        if (member is null || VaultMembers.Count <= 1)
        {
            throw new DomainException("Committed Member removal must retain at least one Vault Member.");
        }

        VaultMembers.Remove(member);
        AddEvent(new VaultMemberAccessRemovedEvent(
            OrganizationId,
            Id,
            userId,
            MemberSequence.Value,
            MutationVersion,
            occurredAt));
    }

    internal void BeginDeletion(Guid deletedBy, string actorName, Instant now)
    {
        if (IsDefault)
        {
            throw new DefaultVaultUndeletableException();
        }

        if (IsDeleting)
        {
            return;
        }

        IsDeleting = true;
        DeletionRequestedBy = deletedBy;
        DeletionRequestedByName = actorName;
        DeletionRequestedAt = now;
        UpdatedBy = deletedBy;
        UpdatedAt = now;
        AdvanceMutationVersion();
    }

    internal void CompleteDeletion()
    {
        if (!IsDeleting
            || DeletionRequestedBy is not { } deletedBy
            || DeletionRequestedAt is not { } deletedAt)
        {
            throw new DomainException("Vault deletion must be durably requested before completion.");
        }

        AddEvent(new VaultDeletedEvent(
            Id,
            deletedBy,
            OrganizationId,
            DeletionRequestedByName ?? string.Empty,
            VaultMembers.Select(x => x.UserId).ToList(),
            MemberSequence.Value,
            MutationVersion,
            deletedAt));
    }

    internal void FenceAccessMutation(Guid updatedBy, Instant now)
    {
        UpdatedBy = updatedBy;
        UpdatedAt = now;
        AdvanceMutationVersion();
    }

    private void AdvanceMutationVersion()
    {
        if (MutationVersion == ulong.MaxValue)
        {
            throw new DomainException("Vault mutation version namespace is exhausted.");
        }

        MutationVersion++;
    }

    private void AddInitialMember(Guid userId, MemberWrappedVaultKey wrappedVaultKey, Instant now) =>
        AddMemberCore(userId, wrappedVaultKey, now);

    private bool AddAgentDiscoveryEnvelope(
        ValidatedAgentDiscoveryProvisioning provisioning,
        Guid provisionedBy,
        uint provisionedAccessEpoch,
        Instant now)
    {
        AgentVaultDiscoveryEnvelopes.Add(AgentVaultDiscoveryEnvelope.Create(
            provisioning,
            provisionedBy,
            provisionedAccessEpoch,
            now));
        return true;
    }

    private void BindAgentTrustAnchors(ValidatedVaultManifest manifest)
    {
        if (ManifestSigningPublicKey is null)
        {
            ManifestSigningPublicKey = manifest.VaultSigningPublicKey.ToArray();
            ManifestSigningKeyFingerprint = manifest.VaultSigningKeyFingerprint.ToArray();
            AgentMessagePublicKey = manifest.VaultAgentMessagePublicKey.ToArray();
            AgentMessageKeyFingerprint = manifest.VaultAgentMessageKeyFingerprint.ToArray();
            return;
        }

        if (!ManifestSigningPublicKey.AsSpan().SequenceEqual(manifest.VaultSigningPublicKey)
            || !ManifestSigningKeyFingerprint!.AsSpan().SequenceEqual(manifest.VaultSigningKeyFingerprint)
            || !AgentMessagePublicKey!.AsSpan().SequenceEqual(manifest.VaultAgentMessagePublicKey)
            || !AgentMessageKeyFingerprint!.AsSpan().SequenceEqual(manifest.VaultAgentMessageKeyFingerprint))
        {
            throw new DomainException("Vault Agent trust anchors cannot change outside an authenticated key rotation.");
        }
    }

    private (VaultMember Member, VaultMemberKeyEnvelope KeyEnvelope) AddMemberCore(
        Guid userId,
        MemberWrappedVaultKey wrappedVaultKey,
        Instant now)
    {
        var member = VaultMember.Create(OrganizationId, Id, userId, now);
        var keyEnvelope = VaultMemberKeyEnvelope.Create(wrappedVaultKey);
        VaultMembers.Add(member);
        VaultMemberKeyEnvelopes.Add(keyEnvelope);

        return (member, keyEnvelope);
    }

    private void EmitUpserted(Guid userId, string actorName, EntityChange change, Instant updatedAt) =>
        AddOrReplaceEvent(new VaultUpsertedEvent(
            Id,
            OrganizationId,
            userId,
            actorName,
            IsDefault,
            change,
            updatedAt));

    private void EmitSyncInvalidated(Instant occurredAt) =>
        AddOrReplaceEvent(new VaultSyncInvalidatedEvent(
            OrganizationId,
            Id,
            MemberSequence.Value,
            MutationVersion,
            occurredAt));

    private static void ValidateMetadata(
        VaultScope scope,
        MemberVaultMetadataCiphertext metadata,
        MemberKeyGeneration memberKeyGeneration,
        VaultKeyVersion currentVaultKeyVersion)
    {
        if (metadata.Scope != scope)
        {
            throw new DomainException("Member Vault metadata scope does not match the Vault.");
        }

        if (metadata.Header.MemberKeyGeneration != memberKeyGeneration
            || metadata.Header.KeyVersion != currentVaultKeyVersion)
        {
            throw new DomainException("Member Vault metadata is not bound to the current key generation.");
        }
    }

    private static void ValidateKeyMaterial(
        VaultScope scope,
        IReadOnlyCollection<VaultKeyMaterialEnvelope> keyMaterial,
        MemberKeyGeneration generation,
        VaultKeyEpoch epoch,
        ulong expectedRevision)
    {
        var expected = new Dictionary<VaultKeyMaterialKind, uint>
        {
            [VaultKeyMaterialKind.DiscoveryKey] = epoch.VdkVersion.Value,
            [VaultKeyMaterialKind.AgentMessagePrivateKey] = epoch.AgentMessageKeyVersion.Value,
            [VaultKeyMaterialKind.ManifestSigningPrivateKey] = epoch.ManifestSigningKeyVersion.Value,
        };
        if (keyMaterial.Count != expected.Count
            || keyMaterial.Select(x => x.Kind).Distinct().Count() != expected.Count
            || keyMaterial.Any(x => x.OrganizationId != scope.OrganizationId
                                    || x.VaultId != scope.VaultId
                                    || x.Revision != expectedRevision
                                    || x.MemberKeyGeneration != generation
                                    || x.WrappingKeyVersion != epoch.VaultKeyVersion
                                    || !expected.TryGetValue(x.Kind, out var version)
                                    || x.KeyVersion != version))
        {
            throw new DomainException("Vault key material must contain one encrypted envelope for every current Vault key purpose.");
        }
    }

    private static void ValidateInitialState(
        MemberVaultMetadataCiphertext metadata,
        MemberKeyGeneration memberKeyGeneration,
        VaultKeyEpoch currentKeyEpoch)
    {
        if (metadata.MetadataRevision.Value != 1
            || memberKeyGeneration.Value != 1
            || currentKeyEpoch.VaultKeyVersion.Value != 1
            || currentKeyEpoch.VdkVersion.Value != 1
            || currentKeyEpoch.AgentMessageKeyVersion.Value != 1
            || currentKeyEpoch.ManifestSigningKeyVersion.Value != 1)
        {
            throw new DomainException(
                "A new Vault must bind ciphertext and key envelopes to server-owned initial revision, generation, and key versions equal to 1.");
        }
    }

    private static void ValidateMemberKey(
        VaultScope scope,
        Guid memberId,
        MemberWrappedVaultKey wrappedVaultKey,
        MemberKeyGeneration memberKeyGeneration,
        VaultKeyVersion currentVaultKeyVersion)
    {
        if (wrappedVaultKey.Scope != scope || wrappedVaultKey.MemberId != memberId)
        {
            throw new DomainException("Wrapped Vault key scope does not match the Vault Member.");
        }

        if (wrappedVaultKey.MemberKeyGeneration != memberKeyGeneration
            || wrappedVaultKey.VaultKeyVersion != currentVaultKeyVersion)
        {
            throw new DomainException("Wrapped Vault key is not bound to the current key generation.");
        }
    }

    private static bool HasSameRotationTrustAnchors(ValidatedVaultManifest left, ValidatedVaultManifest right) =>
        left.ManifestSigningKeyVersion == right.ManifestSigningKeyVersion
        && left.AgentMessageKeyVersion == right.AgentMessageKeyVersion
        && left.VaultSigningPublicKey.AsSpan().SequenceEqual(right.VaultSigningPublicKey)
        && left.VaultSigningKeyFingerprint.AsSpan().SequenceEqual(right.VaultSigningKeyFingerprint)
        && left.VaultAgentMessagePublicKey.AsSpan().SequenceEqual(right.VaultAgentMessagePublicKey)
        && left.VaultAgentMessageKeyFingerprint.AsSpan().SequenceEqual(right.VaultAgentMessageKeyFingerprint);
}
