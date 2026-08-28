using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Api;
using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Core.Types.Exceptions;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Authorization;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record CommitVaultKeyRotationRequest : IRequiresVaultMembership
{
    public Guid VaultId { get; init; }
    public Guid RotationId { get; init; }
    public Guid FencingToken { get; init; }
}

[PublicAPI]
public sealed record VaultKeyRotationIncompleteResponse(
    Guid[] MissingMemberIds,
    Guid[] MissingAgentIds,
    Guid[] MissingFullGrantIds,
    Guid[] DirtyMemberIds,
    Guid[] DirtyAgentIds,
    Guid[] DirtyFullGrantIds,
    RotationEntryKeyIdentity[] MissingEntryKeys,
    Guid[] MissingEntryDiscoveryIds,
    RotationEntryKeyIdentity[] DirtyEntryKeys,
    Guid[] DirtyEntryDiscoveryIds,
    bool VaultMetadataDirty,
    ushort[] MissingKeyMaterialKinds,
    ushort[] DirtyKeyMaterialKinds,
    bool HasMoreIssues);

[PublicAPI]
public sealed record RotationEntryKeyIdentity(Guid EntryId, uint KeyVersion);

[UsedImplicitly]
internal sealed class CommitVaultKeyRotationValidator : Validator<CommitVaultKeyRotationRequest>
{
    public CommitVaultKeyRotationValidator()
    {
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.RotationId).NotEmpty();
        RuleFor(x => x.FencingToken).NotEmpty();
    }
}

[PublicAPI]
internal sealed class CommitVaultKeyRotationEndpoint(
    VaultDomainReadContext domainReadContext,
    VaultDomainWriteContext domainWriteContext,
    VaultPrincipalDeprovisioningCoordinator deprovisioningCoordinator,
    IClock clock) : Endpoint<CommitVaultKeyRotationRequest, VaultKeyRotationResponse>
{
    internal const int CommitPageSize = 20;
    internal const int MaximumReportedIssuesPerCategory = 100;

    public override void Configure()
    {
        Post("api/vaults/{vaultId:guid}/key-rotations/{rotationId:guid}/commit");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.VaultManage);
        this.RequireEmailVerified();
        this.RequireVaultMembership();
        Summary(summary =>
        {
            summary.Summary = "Atomically commit a complete staged Vault key rotation";
            summary.Description = "Takes the short Vault write fence, verifies exact current coverage, rejects dirty preparation and switches every target version in one transaction.";
        });
        Tags("Vault/Key Rotation");
    }

    public override async Task HandleAsync(CommitVaultKeyRotationRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId()!.Value;
        var userId = User.GetUserId()!.Value;
        // Rotation is the exceptional hard-atomic boundary: every prepared envelope and key epoch
        // must become authoritative together or remain entirely on the previous generation.
        await using var transaction = await domainWriteContext.BeginTransactionAsync(ct);
        var vault = await domainWriteContext.LockVault(organizationId, req.VaultId)
            .Include(x => x.KeyMaterialEnvelopes)
            .SingleOrDefaultAsync(ct);
        if (vault is null)
        {
            await transaction.RollbackAsync(ct);
            await Send.NotFoundAsync(ct);
            return;
        }

        var rotation = await domainWriteContext.VaultKeyRotations
            .SingleOrDefaultAsync(x => x.OrganizationId == organizationId
                                       && x.VaultId == req.VaultId
                                       && x.Id == req.RotationId, ct);
        if (rotation is null)
        {
            await transaction.RollbackAsync(ct);
            await Send.NotFoundAsync(ct);
            return;
        }

        if (rotation.Status == VaultKeyRotationStatus.Committed)
        {
            if (rotation.DeprovisioningId is { } deprovisioningId)
            {
                var operation = await domainWriteContext.VaultPrincipalDeprovisionings.SingleAsync(
                    x => x.OrganizationId == organizationId && x.Id == deprovisioningId,
                    ct);
                if (operation.Status == VaultPrincipalDeprovisioningStatus.Completed)
                {
                    operation.ResumeCompletion();
                }
            }

            await domainWriteContext.CommitAsync(transaction, ct);
            await Send.OkAsync(VaultKeyRotationResponses.Map(rotation), ct);
            return;
        }

        if (rotation.ExcludedMemberId == userId)
        {
            await transaction.RollbackAsync(ct);
            AddError(ErrorResponses.General("rotation-removing-principal"));
            await Send.ErrorsAsync(StatusCodes.Status409Conflict, ct);
            return;
        }

        var fenceAcceptedAt = clock.GetCurrentInstant();
        rotation.AssertLease(userId, req.FencingToken, fenceAcceptedAt);
        var prunedItems = await domainWriteContext.PruneStaleVaultKeyRotationItemsAsync(
            organizationId, req.VaultId, req.RotationId, ct);
        prunedItems += await domainWriteContext.PruneExcludedVaultKeyRotationItemsAsync(
            organizationId,
            req.VaultId,
            req.RotationId,
            rotation.ExcludedMemberId,
            rotation.ExcludedAgentId,
            ct);
        var globalItems = await domainWriteContext.VaultKeyRotationPreparedItems
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId
                        && x.VaultId == req.VaultId
                        && x.RotationId == req.RotationId
                        && (x.Kind == VaultKeyRotationPreparedItemKind.VaultMetadata
                            || x.Kind == VaultKeyRotationPreparedItemKind.VaultKeyMaterial
                            || x.Kind == VaultKeyRotationPreparedItemKind.VaultPublicTrustAnchor))
            .ToListAsync(ct);
        var rotatesVaultKey = rotation.Scope.HasFlag(VaultKeyRotationScope.VaultKey);
        var rotatesManifestSigning = rotation.Scope.HasFlag(VaultKeyRotationScope.ManifestSigning);
        var rotatesFullGrantWrappers = rotatesVaultKey || rotatesManifestSigning;
        var rotatesAgentProjection = rotation.Scope.HasFlag(VaultKeyRotationScope.Vdk)
                                     || rotation.Scope.HasFlag(VaultKeyRotationScope.AgentMessage)
                                     || rotatesManifestSigning;
        var trustAnchors = globalItems
            .Where(x => x.Kind == VaultKeyRotationPreparedItemKind.VaultPublicTrustAnchor)
            .ToDictionary(x => (VaultPublicKeyKindContract)x.SubjectVersion,
                x => VaultPreparedPayloadCodec.Decode<VaultPublicKeyContract>(x.Payload));
        ValidatedVaultPublicKey? manifestSigningPublicKey = null;
        if (rotatesManifestSigning
            && trustAnchors.TryGetValue(
                VaultPublicKeyKindContract.ManifestSigningEd25519,
                out var manifestSigningContract))
        {
            manifestSigningPublicKey = VaultEnvelopeContractMapper.ToDomain(
                manifestSigningContract,
                VaultPublicKeyKindContract.ManifestSigningEd25519,
                rotation.TargetKeyEpoch.ManifestSigningKeyVersion.Value);
        }
        var wrapperSigningAnchor = rotatesManifestSigning
            ? manifestSigningPublicKey
            : new ValidatedVaultPublicKey(
                VaultPublicKeyKind.ManifestSigningEd25519,
                vault.CurrentManifestSigningKeyVersion.Value,
                vault.ManifestSigningPublicKey,
                vault.ManifestSigningKeyFingerprint);
        var memberCoverage = rotatesVaultKey
            ? await InspectMemberCoverageAsync(
                organizationId, req.VaultId, req.RotationId, rotation.ExcludedMemberId, ct)
            : MemberCoverage.Complete;
        var agentCoverage = rotatesAgentProjection
            ? await InspectAgentCoverageAsync(
                organizationId, req.VaultId, req.RotationId, rotation.ExcludedAgentId, ct)
            : AgentCoverage.Complete;
        var fullGrantCoverage = rotatesFullGrantWrappers && wrapperSigningAnchor is not null
            ? await InspectFullGrantCoverageAsync(
                organizationId, req.VaultId, req.RotationId, rotation.ExcludedAgentId,
                rotation.TargetKeyEpoch.VaultKeyVersion.Value,
                wrapperSigningAnchor.Version,
                wrapperSigningAnchor.Fingerprint,
                wrapperSigningAnchor.PublicKey,
                ct)
            : FullGrantCoverage.Complete;
        var entryCoverage = await InspectEntryCoverageAsync(
            organizationId, req.VaultId, req.RotationId, rotatesVaultKey,
            rotation.Scope.HasFlag(VaultKeyRotationScope.Vdk), ct);
        var coverage = InspectCoverage(
            vault, rotation, globalItems, memberCoverage, agentCoverage, fullGrantCoverage, entryCoverage);
        if (!coverage.IsComplete)
        {
            if (prunedItems > 0)
            {
                await domainWriteContext.CommitAsync(transaction, ct);
            }
            else
            {
                await transaction.RollbackAsync(ct);
            }
            await Send.ResultAsync(Results.Json(coverage.Response, statusCode: StatusCodes.Status409Conflict));
            return;
        }

        var metadata = rotatesVaultKey
            ? DecodeSingle<MemberVaultMetadataEnvelopeContract>(globalItems,
                VaultKeyRotationPreparedItemKind.VaultMetadata, req.VaultId)
            : null;
        var keyMaterial = globalItems
            .Where(x => x.Kind == VaultKeyRotationPreparedItemKind.VaultKeyMaterial)
            .Select(DecodeKeyMaterial)
            .ToArray();
        var agentMessagePublicKey = rotation.Scope.HasFlag(VaultKeyRotationScope.AgentMessage)
            ? VaultEnvelopeContractMapper.ToDomain(
                trustAnchors[VaultPublicKeyKindContract.AgentMessageX25519],
                VaultPublicKeyKindContract.AgentMessageX25519,
                rotation.TargetKeyEpoch.AgentMessageKeyVersion.Value)
            : null;
        var targetMemberKeyGeneration = rotation.TargetMemberKeyGeneration;
        var targetKeyEpoch = rotation.TargetKeyEpoch;
        var retiredAgentMessageKeyVersion = vault.CurrentAgentMessageKeyVersion.Value;
        var committedAt = clock.GetCurrentInstant();
        domainWriteContext.Clear();
        if (rotatesVaultKey)
        {
            await ApplyMemberRotationPagesAsync(
                organizationId, req.VaultId, req.RotationId,
                targetMemberKeyGeneration, targetKeyEpoch.VaultKeyVersion, rotation.ExcludedMemberId, ct);
        }
        if (rotatesFullGrantWrappers)
        {
            await ApplyFullGrantRotationPagesAsync(
                organizationId, req.VaultId, req.RotationId,
                targetKeyEpoch.VaultKeyVersion.Value,
                rotation.ExcludedAgentId,
                wrapperSigningAnchor!.Version,
                wrapperSigningAnchor.Fingerprint,
                wrapperSigningAnchor.PublicKey,
                ct);
        }
        if (rotatesManifestSigning)
        {
            await RevokeScriptExecutionGrantsForSigningRotationAsync(
                organizationId, req.VaultId, committedAt, ct);
        }
        var agentManifest = rotatesAgentProjection
            ? await ApplyAgentRotationPagesAsync(
                organizationId, req.VaultId, req.RotationId,
                targetKeyEpoch, userId, committedAt, rotation.ExcludedAgentId, ct)
            : null;
        await ApplyEntryRotationPagesAsync(
            organizationId,
            req.VaultId,
            req.RotationId,
            targetMemberKeyGeneration,
            targetKeyEpoch,
            rotatesVaultKey,
            rotation.Scope.HasFlag(VaultKeyRotationScope.Vdk),
            userId,
            committedAt,
            ct);
        if (rotation.Scope.HasFlag(VaultKeyRotationScope.AgentMessage))
        {
            await InvalidatePendingGrantReasonPagesAsync(
                organizationId, req.VaultId, retiredAgentMessageKeyVersion, ct);
        }

        vault = await domainWriteContext.LockVault(organizationId, req.VaultId)
            .Include(x => x.KeyMaterialEnvelopes)
            .SingleAsync(ct);
        rotation = await domainWriteContext.VaultKeyRotations.SingleAsync(
            x => x.OrganizationId == organizationId
                 && x.VaultId == req.VaultId
                 && x.Id == req.RotationId,
            ct);
        var finalizedAt = clock.GetCurrentInstant();
        rotation.AssertLease(userId, req.FencingToken, fenceAcceptedAt);
        vault.CommitKeyRotation(
            rotation,
            metadata is null ? null : VaultEnvelopeContractMapper.ToDomain(metadata),
            keyMaterial,
            agentManifest,
            agentMessagePublicKey,
            manifestSigningPublicKey,
            userId,
            finalizedAt);
        rotation.MarkReady(userId, req.FencingToken, fenceAcceptedAt);
        rotation.MarkCommitted(finalizedAt);
        await CompleteDeprovisioningStepAsync(vault, rotation, finalizedAt, ct);
        await domainWriteContext.CommitAsync(transaction, ct);
        await Send.OkAsync(VaultKeyRotationResponses.Map(rotation), ct);
    }

    private async Task CompleteDeprovisioningStepAsync(
        Domain.Vault vault,
        VaultKeyRotation rotation,
        Instant completedAt,
        CancellationToken cancellationToken)
    {
        if (rotation.DeprovisioningId is null)
        {
            await domainWriteContext.FlushAsync(cancellationToken);
            domainWriteContext.Clear();
            await deprovisioningCoordinator.StartNextPendingAsync(
                rotation.OrganizationId, completedAt, cancellationToken);
            return;
        }

        var operation = await domainWriteContext.VaultPrincipalDeprovisionings.SingleAsync(
            x => x.OrganizationId == rotation.OrganizationId && x.Id == rotation.DeprovisioningId,
            cancellationToken);
        if (rotation.ExcludedMemberId is { } memberId)
        {
            vault = await domainWriteContext.Vaults
                .Include(x => x.VaultMembers)
                .Include(x => x.VaultMemberKeyEnvelopes.Where(envelope => envelope.MemberId == memberId))
                .SingleAsync(x => x.OrganizationId == rotation.OrganizationId && x.Id == rotation.VaultId,
                    cancellationToken);
            vault.RemoveMemberForCommittedRotation(memberId, completedAt);
        }

        if (rotation.ExcludedAgentId is { } agentId)
        {
            await DeleteAgentGrantEnvelopePagesAsync(
                rotation.OrganizationId, rotation.VaultId, agentId, cancellationToken);
            operation = await domainWriteContext.VaultPrincipalDeprovisionings.SingleAsync(
                x => x.OrganizationId == rotation.OrganizationId && x.Id == rotation.DeprovisioningId,
                cancellationToken);
            vault = await domainWriteContext.Vaults
                .Include(x => x.AgentVaultDiscoveryEnvelopes.Where(envelope => envelope.AgentId == agentId))
                .SingleAsync(x => x.OrganizationId == rotation.OrganizationId && x.Id == rotation.VaultId,
                    cancellationToken);
            vault.RemoveAgentDiscoveryPage(new HashSet<Guid> { agentId });
        }

        operation.CompleteCurrentVault(rotation.VaultId, rotation.Id, completedAt);
        await domainWriteContext.FlushAsync(cancellationToken);
        domainWriteContext.Clear();
        operation = await domainWriteContext.VaultPrincipalDeprovisionings.SingleAsync(
            x => x.OrganizationId == rotation.OrganizationId && x.Id == rotation.DeprovisioningId,
            cancellationToken);
        await deprovisioningCoordinator.AdvanceAsync(operation, completedAt, cancellationToken);
        if (operation.Status == VaultPrincipalDeprovisioningStatus.Completed)
        {
            await deprovisioningCoordinator.StartNextPendingAsync(
                operation.OrganizationId, completedAt, cancellationToken, operation.Id);
        }
    }

    private async Task DeleteAgentGrantEnvelopePagesAsync(
        Guid organizationId,
        Guid vaultId,
        Guid agentId,
        CancellationToken cancellationToken)
    {
        Guid? lastGrantId = null;
        while (true)
        {
            var query = domainWriteContext.Grants
                .Include(x => x.AgentWrappedVaultKey)
                .Include(x => x.ScriptExecutionPackage)
                .Include(x => x.GrantEntryScopes).ThenInclude(x => x.Envelope)
                .Where(x => x.OrganizationId == organizationId
                            && x.VaultId == vaultId
                            && x.AgentId == agentId
                            && (x.AgentWrappedVaultKey != null
                                || x.ScriptExecutionPackage != null
                                || x.GrantEntryScopes.Any(scope => scope.Envelope != null)));
            if (lastGrantId is not null)
            {
                query = query.Where(x => x.Id.CompareTo(lastGrantId.Value) > 0);
            }

            var grants = await query.OrderBy(x => x.Id).Take(CommitPageSize).ToListAsync(cancellationToken);
            if (grants.Count == 0)
            {
                break;
            }

            foreach (var grant in grants)
            {
                grant.DeleteAgentEnvelopes();
            }

            lastGrantId = grants[^1].Id;
            await domainWriteContext.FlushAsync(cancellationToken);
            domainWriteContext.Clear();
            if (grants.Count < CommitPageSize)
            {
                break;
            }
        }
    }

    private async Task<MemberCoverage> InspectMemberCoverageAsync(
        Guid organizationId,
        Guid vaultId,
        Guid rotationId,
        Guid? excludedMemberId,
        CancellationToken cancellationToken)
    {
        var missing = new List<Guid>();
        var dirty = new List<Guid>();
        var isComplete = true;
        var hasMoreIssues = false;
        Guid? lastMemberId = null;
        while (true)
        {
            var query = domainWriteContext.VaultMembers.AsNoTracking()
                .Where(x => x.OrganizationId == organizationId && x.VaultId == vaultId)
                .Where(x => excludedMemberId == null || x.UserId != excludedMemberId);
            if (lastMemberId is not null)
            {
                query = query.Where(x => x.UserId.CompareTo(lastMemberId.Value) > 0);
            }

            var memberIds = await query.OrderBy(x => x.UserId)
                .Select(x => x.UserId)
                .Take(CommitPageSize)
                .ToListAsync(cancellationToken);
            if (memberIds.Count == 0)
            {
                break;
            }

            var directory = await domainWriteContext.LockMemberKeyDirectory(memberIds.ToArray())
                .ToDictionaryAsync(x => x.UserId, cancellationToken);
            var items = await LoadPreparedItemsAsync(
                organizationId, vaultId, rotationId,
                VaultKeyRotationPreparedItemKind.MemberVaultKey, memberIds.ToArray(), cancellationToken);
            foreach (var memberId in memberIds)
            {
                var item = items.SingleOrDefault(x => x.SubjectId == memberId);
                if (item is null)
                {
                    isComplete = false;
                    AddBounded(missing, memberId, ref hasMoreIssues);
                    continue;
                }

                var envelope = VaultEnvelopeContractMapper.ToDomain(
                    VaultPreparedPayloadCodec.Decode<MemberVaultKeyEnvelopeContract>(item.Payload));
                if (!directory.TryGetValue(memberId, out var recipient) || !recipient.Matches(envelope))
                {
                    isComplete = false;
                    AddBounded(dirty, memberId, ref hasMoreIssues);
                }
            }

            lastMemberId = memberIds[^1];
            domainWriteContext.Clear();
            if (memberIds.Count < CommitPageSize)
            {
                break;
            }
        }

        return new MemberCoverage(isComplete, missing.ToArray(), dirty.ToArray(), hasMoreIssues);
    }

    private async Task<AgentCoverage> InspectAgentCoverageAsync(
        Guid organizationId,
        Guid vaultId,
        Guid rotationId,
        Guid? excludedAgentId,
        CancellationToken cancellationToken)
    {
        var missing = new List<Guid>();
        var dirty = new List<Guid>();
        var isComplete = true;
        var hasMoreIssues = false;
        Guid? lastAgentId = null;
        while (true)
        {
            var query = domainWriteContext.Agents.AsNoTracking()
                .Where(x => x.OrganizationId == organizationId && x.Status == AgentStatus.Active)
                .Where(x => excludedAgentId == null || x.Id != excludedAgentId);
            if (lastAgentId is not null)
            {
                query = query.Where(x => x.Id.CompareTo(lastAgentId.Value) > 0);
            }

            var agents = await query.OrderBy(x => x.Id).Take(CommitPageSize).ToListAsync(cancellationToken);
            if (agents.Count == 0)
            {
                break;
            }

            var agentIds = agents.Select(x => x.Id).ToArray();
            var items = await LoadPreparedItemsAsync(
                organizationId, vaultId, rotationId,
                VaultKeyRotationPreparedItemKind.AgentDiscoveryEnvelope, agentIds, cancellationToken);
            var revisions = await domainWriteContext.AgentVaultDiscoveryEnvelopes.AsNoTracking()
                .Where(x => x.OrganizationId == organizationId && x.VaultId == vaultId && agentIds.Contains(x.AgentId))
                .ToDictionaryAsync(x => x.AgentId, x => x.ManifestRevision.Value, cancellationToken);
            foreach (var agent in agents)
            {
                var item = items.SingleOrDefault(x => x.SubjectId == agent.Id);
                if (item is null)
                {
                    isComplete = false;
                    AddBounded(missing, agent.Id, ref hasMoreIssues);
                }
                else if (item.SourceRevision != revisions.GetValueOrDefault(agent.Id))
                {
                    isComplete = false;
                    AddBounded(dirty, agent.Id, ref hasMoreIssues);
                }
            }

            lastAgentId = agents[^1].Id;
            if (agents.Count < CommitPageSize)
            {
                break;
            }
        }

        return new AgentCoverage(isComplete, missing.ToArray(), dirty.ToArray(), hasMoreIssues);
    }

    private async Task<EntryCoverage> InspectEntryCoverageAsync(
        Guid organizationId,
        Guid vaultId,
        Guid rotationId,
        bool requireEntryKeys,
        bool requireDiscovery,
        CancellationToken cancellationToken)
    {
        if (!requireEntryKeys && !requireDiscovery)
        {
            return EntryCoverage.Complete;
        }

        var missingKeys = new List<RotationEntryKeyIdentity>();
        var missingDiscovery = new List<Guid>();
        var dirtyKeys = new List<RotationEntryKeyIdentity>();
        var dirtyDiscovery = new List<Guid>();
        var isComplete = true;
        var hasMoreIssues = false;
        Guid? lastEntryId = null;

        while (true)
        {
            IQueryable<VaultEntry> query = domainWriteContext.Entries.AsNoTracking()
                .Where(x => x.OrganizationId == organizationId && x.VaultId == vaultId);
            if (requireEntryKeys)
            {
                query = query.Include(x => x.Keys);
            }
            else
            {
                query = query.Where(x => x.AgentDiscoveryRevision != null);
            }
            if (lastEntryId is not null)
            {
                query = query.Where(x => x.Id.CompareTo(lastEntryId.Value) > 0);
            }

            var entries = await query.OrderBy(x => x.Id).Take(CommitPageSize).ToListAsync(cancellationToken);
            if (entries.Count == 0)
            {
                break;
            }
            var entryIds = entries.Select(x => x.Id).ToArray();
            var items = await LoadEntryPreparedItemsAsync(
                organizationId, vaultId, rotationId, entryIds, cancellationToken);
            foreach (var entry in entries)
            {
                foreach (var key in entry.Keys.OrderBy(x => x.KeyVersion.Value))
                {
                    if (!requireEntryKeys)
                    {
                        break;
                    }
                    var item = items.SingleOrDefault(x => x.Kind == VaultKeyRotationPreparedItemKind.EntryKey
                        && x.SubjectId == entry.Id && x.SubjectVersion == key.KeyVersion.Value);
                    var identity = new RotationEntryKeyIdentity(entry.Id, key.KeyVersion.Value);
                    if (item is null)
                    {
                        isComplete = false;
                        AddBounded(missingKeys, identity, ref hasMoreIssues);
                    }
                    else if (item.SourceRevision != key.WrapperRevision.Value)
                    {
                        isComplete = false;
                        AddBounded(dirtyKeys, identity, ref hasMoreIssues);
                    }
                }

                if (!requireDiscovery || entry.AgentDiscoveryRevision is not { } revision)
                {
                    continue;
                }

                var discoveryItem = items.SingleOrDefault(x =>
                    x.Kind == VaultKeyRotationPreparedItemKind.EntryDiscovery && x.SubjectId == entry.Id);
                if (discoveryItem is null)
                {
                    isComplete = false;
                    AddBounded(missingDiscovery, entry.Id, ref hasMoreIssues);
                }
                else if (discoveryItem.SourceRevision != revision.Value)
                {
                    isComplete = false;
                    AddBounded(dirtyDiscovery, entry.Id, ref hasMoreIssues);
                }
            }

            lastEntryId = entries[^1].Id;
            if (entries.Count < CommitPageSize)
            {
                break;
            }
        }

        return new EntryCoverage(
            isComplete,
            missingKeys.ToArray(),
            missingDiscovery.ToArray(),
            dirtyKeys.ToArray(),
            dirtyDiscovery.ToArray(),
            hasMoreIssues);
    }

    private async Task<FullGrantCoverage> InspectFullGrantCoverageAsync(
        Guid organizationId,
        Guid vaultId,
        Guid rotationId,
        Guid? excludedAgentId,
        uint targetVaultKeyVersion,
        uint expectedSigningKeyVersion,
        byte[] expectedSigningKeyFingerprint,
        byte[] expectedSigningPublicKey,
        CancellationToken cancellationToken)
    {
        var missing = new List<Guid>();
        var dirty = new List<Guid>();
        var isComplete = true;
        var hasMoreIssues = false;
        Guid? lastGrantId = null;
        while (true)
        {
            var query = domainWriteContext.Grants
                .OfType<FullGrant>()
                .AsNoTracking()
                .Include(x => x.AgentWrappedVaultKey)
                .Where(x => x.OrganizationId == organizationId
                    && x.VaultId == vaultId
                    && x.Status == GrantStatus.Active)
                .Where(x => excludedAgentId == null || x.AgentId != excludedAgentId);
            if (lastGrantId is not null)
            {
                query = query.Where(x => x.Id.CompareTo(lastGrantId.Value) > 0);
            }

            var grants = await query.OrderBy(x => x.Id).Take(CommitPageSize).ToListAsync(cancellationToken);
            if (grants.Count == 0)
            {
                break;
            }

            var grantIds = grants.Select(x => x.Id).ToArray();
            var agentIds = grants.Select(x => x.AgentId).Distinct().ToArray();
            var agents = await domainWriteContext.Agents.AsNoTracking()
                .Where(x => x.OrganizationId == organizationId && agentIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, cancellationToken);
            var items = await LoadPreparedItemsAsync(
                organizationId, vaultId, rotationId,
                VaultKeyRotationPreparedItemKind.AgentWrappedVaultKey, grantIds, cancellationToken);
            foreach (var grant in grants)
            {
                var item = items.SingleOrDefault(x => x.SubjectId == grant.Id);
                if (item is null)
                {
                    isComplete = false;
                    AddBounded(missing, grant.Id, ref hasMoreIssues);
                    continue;
                }

                try
                {
                    if (grant.AgentWrappedVaultKey is null
                        || item.SourceRevision != grant.AgentWrappedVaultKey.VaultKeyVersion.Value
                        || !agents.TryGetValue(grant.AgentId, out var agent)
                        || agent.Status != AgentStatus.Active
                        || agent.AccessEpoch != grant.AgentAccessEpoch)
                    {
                        throw new DomainException("FULL grant rotation source is stale.");
                    }

                    var fingerprint = VaultKeyFingerprint.Compute(
                        Convert.FromBase64String(agent.PublicKey), VaultKeyKind.AgentX25519);
                    _ = AgentWrappedVaultKeyContractMapper.ToDomain(
                        VaultPreparedPayloadCodec.Decode<AgentWrappedVaultKeyContract>(item.Payload),
                        organizationId, vaultId, grant.Id, grant.AgentId, grant.AgentAccessEpoch,
                        targetVaultKeyVersion, agent.RecipientKeyVersion, fingerprint,
                        expectedSigningKeyVersion,
                        expectedSigningKeyFingerprint,
                        expectedSigningPublicKey);
                }
                catch (Exception ex) when (ex is DomainException or FormatException)
                {
                    isComplete = false;
                    AddBounded(dirty, grant.Id, ref hasMoreIssues);
                }
            }

            lastGrantId = grants[^1].Id;
            domainWriteContext.Clear();
            if (grants.Count < CommitPageSize)
            {
                break;
            }
        }

        return new FullGrantCoverage(isComplete, missing.ToArray(), dirty.ToArray(), hasMoreIssues);
    }

    private async Task ApplyFullGrantRotationPagesAsync(
        Guid organizationId,
        Guid vaultId,
        Guid rotationId,
        uint targetVaultKeyVersion,
        Guid? excludedAgentId,
        uint expectedSigningKeyVersion,
        byte[] expectedSigningKeyFingerprint,
        byte[] expectedSigningPublicKey,
        CancellationToken cancellationToken)
    {
        Guid? lastGrantId = null;
        while (true)
        {
            var query = domainWriteContext.Grants
                .OfType<FullGrant>()
                .Include(x => x.AgentWrappedVaultKey)
                .Where(x => x.OrganizationId == organizationId
                    && x.VaultId == vaultId
                    && x.Status == GrantStatus.Active)
                .Where(x => excludedAgentId == null || x.AgentId != excludedAgentId);
            if (lastGrantId is not null)
            {
                query = query.Where(x => x.Id.CompareTo(lastGrantId.Value) > 0);
            }

            var grants = await query.OrderBy(x => x.Id).Take(CommitPageSize).ToListAsync(cancellationToken);
            if (grants.Count == 0)
            {
                break;
            }

            var grantIds = grants.Select(x => x.Id).ToArray();
            var agentIds = grants.Select(x => x.AgentId).Distinct().ToArray();
            var agents = await domainWriteContext.Agents
                .Where(x => x.OrganizationId == organizationId && agentIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, cancellationToken);
            var items = await LoadPreparedItemsAsync(
                organizationId, vaultId, rotationId,
                VaultKeyRotationPreparedItemKind.AgentWrappedVaultKey, grantIds, cancellationToken);
            foreach (var grant in grants)
            {
                var agent = agents[grant.AgentId];
                var item = items.Single(x => x.SubjectId == grant.Id);
                var fingerprint = VaultKeyFingerprint.Compute(
                    Convert.FromBase64String(agent.PublicKey), VaultKeyKind.AgentX25519);
                var replacement = AgentWrappedVaultKeyContractMapper.ToDomain(
                    VaultPreparedPayloadCodec.Decode<AgentWrappedVaultKeyContract>(item.Payload),
                    organizationId, vaultId, grant.Id, grant.AgentId, grant.AgentAccessEpoch,
                    targetVaultKeyVersion, agent.RecipientKeyVersion, fingerprint,
                    expectedSigningKeyVersion,
                    expectedSigningKeyFingerprint,
                    expectedSigningPublicKey);
                grant.AgentWrappedVaultKey!.ReplaceWith(replacement);
            }

            domainWriteContext.EnsureRotationPageTrackingIsBounded(0, CommitPageSize);
            lastGrantId = grants[^1].Id;
            await domainWriteContext.FlushAsync(cancellationToken);
            domainWriteContext.Clear();
            if (grants.Count < CommitPageSize)
            {
                break;
            }
        }
    }

    private async Task RevokeScriptExecutionGrantsForSigningRotationAsync(
        Guid organizationId,
        Guid vaultId,
        Instant now,
        CancellationToken cancellationToken)
    {
        Guid? lastGrantId = null;
        while (true)
        {
            var query = domainWriteContext.Grants
                .OfType<ScriptExecutionGrant>()
                .Include(grant => grant.ScriptExecutionPackage)
                .Where(grant => grant.OrganizationId == organizationId
                                && grant.VaultId == vaultId
                                && grant.Status == GrantStatus.Active);
            if (lastGrantId is not null)
            {
                query = query.Where(grant => grant.Id.CompareTo(lastGrantId.Value) > 0);
            }

            var grants = await query.OrderBy(grant => grant.Id)
                .Take(CommitPageSize)
                .ToListAsync(cancellationToken);
            if (grants.Count == 0)
            {
                break;
            }

            var agentIds = grants.Select(grant => grant.AgentId).Distinct().ToArray();
            var agentNames = await domainReadContext.Agents
                .Where(agent => agent.OrganizationId == organizationId
                                && agentIds.Contains(agent.Id))
                .Select(agent => new { agent.Id, agent.Name })
                .ToDictionaryAsync(agent => agent.Id, agent => agent.Name, cancellationToken);
            foreach (var grant in grants)
            {
                grant.RevokeBySystem(
                    new GrantNames(
                        agentNames.GetValueOrDefault(grant.AgentId) ?? GrantNames.UnknownAgent,
                        GrantNames.UnknownEntry,
                        string.Empty,
                        GrantNames.SystemActor),
                    now);
                grant.DeleteAgentEnvelopes();
            }

            domainWriteContext.EnsureRotationPageTrackingIsBounded(0, CommitPageSize);
            lastGrantId = grants[^1].Id;
            await domainWriteContext.FlushAsync(cancellationToken);
            domainWriteContext.Clear();
            if (grants.Count < CommitPageSize)
            {
                break;
            }
        }
    }
    private async Task ApplyEntryRotationPagesAsync(
        Guid organizationId,
        Guid vaultId,
        Guid rotationId,
        MemberKeyGeneration targetMemberKeyGeneration,
        VaultKeyEpoch targetKeyEpoch,
        bool rewrapEntryKeys,
        bool rotateDiscovery,
        Guid committedBy,
        Instant committedAt,
        CancellationToken cancellationToken)
    {
        if (!rewrapEntryKeys && !rotateDiscovery)
        {
            return;
        }

        Guid? lastEntryId = null;
        while (true)
        {
            IQueryable<VaultEntry> query = domainWriteContext.Entries
                .Where(x => x.OrganizationId == organizationId && x.VaultId == vaultId);
            if (rewrapEntryKeys)
            {
                query = query.Include(x => x.Keys);
            }
            else
            {
                query = query.Where(x => x.AgentDiscoveryRevision != null);
            }
            if (lastEntryId is not null)
            {
                query = query.Where(x => x.Id.CompareTo(lastEntryId.Value) > 0);
            }

            var entries = await query.OrderBy(x => x.Id).Take(CommitPageSize).ToListAsync(cancellationToken);
            if (entries.Count == 0)
            {
                break;
            }
            domainWriteContext.EnsureRotationPageTrackingIsBounded(CommitPageSize, 0);

            var entryIds = entries.Select(x => x.Id).ToArray();
            var items = await LoadEntryPreparedItemsAsync(
                organizationId, vaultId, rotationId, entryIds, cancellationToken);
            foreach (var entry in entries)
            {
                var rewrappedKeys = items
                    .Where(x => x.Kind == VaultKeyRotationPreparedItemKind.EntryKey && x.SubjectId == entry.Id)
                    .Select(x => VaultEnvelopeContractMapper.ToDomain(
                        VaultPreparedPayloadCodec.Decode<VaultEntryKeyContract>(x.Payload)))
                    .ToArray();
                var discoveryItem = items.SingleOrDefault(x =>
                    x.Kind == VaultKeyRotationPreparedItemKind.EntryDiscovery && x.SubjectId == entry.Id);
                var discovery = discoveryItem is null
                    ? null
                    : VaultEnvelopeContractMapper.ToDomain(
                        VaultPreparedPayloadCodec.Decode<AgentDiscoveryEnvelopeContract>(discoveryItem.Payload));
                entry.CommitKeyRotation(
                    rewrappedKeys,
                    discovery,
                    rewrapEntryKeys,
                    rotateDiscovery,
                    targetMemberKeyGeneration,
                    targetKeyEpoch.VaultKeyVersion,
                    targetKeyEpoch.VdkVersion,
                    committedBy,
                    committedAt);
            }

            lastEntryId = entries[^1].Id;
            await domainWriteContext.FlushAsync(cancellationToken);
            domainWriteContext.Clear();
            if (entries.Count < CommitPageSize)
            {
                break;
            }
        }
    }

    private async Task ApplyMemberRotationPagesAsync(
        Guid organizationId,
        Guid vaultId,
        Guid rotationId,
        MemberKeyGeneration targetGeneration,
        VaultKeyVersion targetVaultKeyVersion,
        Guid? excludedMemberId,
        CancellationToken cancellationToken)
    {
        Guid? lastMemberId = null;
        while (true)
        {
            var query = domainWriteContext.VaultMembers.AsNoTracking()
                .Where(x => x.OrganizationId == organizationId && x.VaultId == vaultId)
                .Where(x => excludedMemberId == null || x.UserId != excludedMemberId);
            if (lastMemberId is not null)
            {
                query = query.Where(x => x.UserId.CompareTo(lastMemberId.Value) > 0);
            }

            var memberIds = await query.OrderBy(x => x.UserId)
                .Select(x => x.UserId)
                .Take(CommitPageSize)
                .ToListAsync(cancellationToken);
            if (memberIds.Count == 0)
            {
                break;
            }

            var items = await LoadPreparedItemsAsync(
                organizationId, vaultId, rotationId,
                VaultKeyRotationPreparedItemKind.MemberVaultKey, memberIds.ToArray(), cancellationToken);
            var memberKeys = items.Select(x => VaultEnvelopeContractMapper.ToDomain(
                VaultPreparedPayloadCodec.Decode<MemberVaultKeyEnvelopeContract>(x.Payload))).ToArray();
            var vault = await domainWriteContext.Vaults.SingleAsync(
                x => x.OrganizationId == organizationId && x.Id == vaultId, cancellationToken);
            vault.AddRotatedMemberKeyPage(targetGeneration, targetVaultKeyVersion, memberKeys);
            domainWriteContext.EnsureRotationRecipientTrackingIsBounded(CommitPageSize, 0);
            lastMemberId = memberIds[^1];
            await domainWriteContext.FlushAsync(cancellationToken);
            domainWriteContext.Clear();
            if (memberIds.Count < CommitPageSize)
            {
                break;
            }
        }
    }

    private async Task<ValidatedVaultManifest?> ApplyAgentRotationPagesAsync(
        Guid organizationId,
        Guid vaultId,
        Guid rotationId,
        VaultKeyEpoch targetEpoch,
        Guid committedBy,
        Instant committedAt,
        Guid? excludedAgentId,
        CancellationToken cancellationToken)
    {
        ValidatedVaultManifest? manifest = null;
        Guid? lastAgentId = null;
        while (true)
        {
            var query = domainWriteContext.Agents.AsNoTracking()
                .Where(x => x.OrganizationId == organizationId && x.Status == AgentStatus.Active)
                .Where(x => excludedAgentId == null || x.Id != excludedAgentId);
            if (lastAgentId is not null)
            {
                query = query.Where(x => x.Id.CompareTo(lastAgentId.Value) > 0);
            }

            var agents = await query.OrderBy(x => x.Id).Take(CommitPageSize).ToListAsync(cancellationToken);
            if (agents.Count == 0)
            {
                break;
            }

            var agentIds = agents.Select(x => x.Id).ToArray();
            var agentById = agents.ToDictionary(x => x.Id);
            var items = await LoadPreparedItemsAsync(
                organizationId, vaultId, rotationId,
                VaultKeyRotationPreparedItemKind.AgentDiscoveryEnvelope, agentIds, cancellationToken);
            var targets = items.Select(x => VaultPreparedPayloadCodec.Decode<RotationAgentDiscoveryContract>(x.Payload))
                .Select(x =>
                {
                    var agent = agentById[x.AgentId];
                    return (VaultManifestCryptoValidator.Validate(x.Envelope, x.Manifest, agent), agent.AccessEpoch);
                })
                .ToArray();
            var vault = await domainWriteContext.Vaults
                .Include(x => x.AgentVaultDiscoveryEnvelopes.Where(envelope => agentIds.Contains(envelope.AgentId)))
                .SingleAsync(x => x.OrganizationId == organizationId && x.Id == vaultId, cancellationToken);
            manifest = vault.ApplyRotatedAgentDiscoveryPage(
                targetEpoch, targets, manifest, committedBy, committedAt);
            domainWriteContext.EnsureRotationRecipientTrackingIsBounded(0, CommitPageSize);
            lastAgentId = agents[^1].Id;
            await domainWriteContext.FlushAsync(cancellationToken);
            domainWriteContext.Clear();
            if (agents.Count < CommitPageSize)
            {
                break;
            }
        }

        await RemoveInactiveAgentDiscoveryPagesAsync(organizationId, vaultId, cancellationToken);
        return manifest;
    }

    private async Task RemoveInactiveAgentDiscoveryPagesAsync(
        Guid organizationId,
        Guid vaultId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var agentIds = await domainWriteContext.AgentVaultDiscoveryEnvelopes.AsNoTracking()
                .Where(x => x.OrganizationId == organizationId && x.VaultId == vaultId)
                .Where(x => !domainWriteContext.Agents.Any(agent =>
                    agent.OrganizationId == organizationId
                    && agent.Id == x.AgentId
                    && agent.Status == AgentStatus.Active))
                .OrderBy(x => x.AgentId)
                .Select(x => x.AgentId)
                .Take(CommitPageSize)
                .ToListAsync(cancellationToken);
            if (agentIds.Count == 0)
            {
                break;
            }

            var ids = agentIds.ToHashSet();
            var vault = await domainWriteContext.Vaults
                .Include(x => x.AgentVaultDiscoveryEnvelopes.Where(envelope => ids.Contains(envelope.AgentId)))
                .SingleAsync(x => x.OrganizationId == organizationId && x.Id == vaultId, cancellationToken);
            vault.RemoveAgentDiscoveryPage(ids);
            domainWriteContext.EnsureRotationRecipientTrackingIsBounded(0, CommitPageSize);
            await domainWriteContext.FlushAsync(cancellationToken);
            domainWriteContext.Clear();
        }
    }

    private async Task InvalidatePendingGrantReasonPagesAsync(
        Guid organizationId,
        Guid vaultId,
        uint retiredAgentMessageKeyVersion,
        CancellationToken cancellationToken)
    {
        Guid? lastGrantId = null;
        while (true)
        {
            var query = domainWriteContext.Grants
                .Include(x => x.EncryptedReason)
                .Where(x => x.OrganizationId == organizationId
                            && x.VaultId == vaultId
                            && x.Status == GrantStatus.Pending);
            if (lastGrantId is not null)
            {
                query = query.Where(x => x.Id.CompareTo(lastGrantId.Value) > 0);
            }

            var grants = await query.OrderBy(x => x.Id).Take(CommitPageSize).ToListAsync(cancellationToken);
            if (grants.Count == 0)
            {
                break;
            }
            domainWriteContext.EnsureRotationPageTrackingIsBounded(0, CommitPageSize);

            foreach (var grant in grants)
            {
                grant.InvalidateReasonEncryptedToRetiredAgentMessageKey(retiredAgentMessageKeyVersion);
            }

            lastGrantId = grants[^1].Id;
            await domainWriteContext.FlushAsync(cancellationToken);
            domainWriteContext.Clear();
            if (grants.Count < CommitPageSize)
            {
                break;
            }
        }
    }

    private Task<List<VaultKeyRotationPreparedItem>> LoadEntryPreparedItemsAsync(
        Guid organizationId,
        Guid vaultId,
        Guid rotationId,
        Guid[] entryIds,
        CancellationToken cancellationToken) =>
        domainWriteContext.VaultKeyRotationPreparedItems.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId
                        && x.VaultId == vaultId
                        && x.RotationId == rotationId
                        && entryIds.Contains(x.SubjectId)
                        && (x.Kind == VaultKeyRotationPreparedItemKind.EntryKey
                            || x.Kind == VaultKeyRotationPreparedItemKind.EntryDiscovery))
            .ToListAsync(cancellationToken);

    private Task<List<VaultKeyRotationPreparedItem>> LoadPreparedItemsAsync(
        Guid organizationId,
        Guid vaultId,
        Guid rotationId,
        VaultKeyRotationPreparedItemKind kind,
        Guid[] subjectIds,
        CancellationToken cancellationToken) =>
        domainWriteContext.VaultKeyRotationPreparedItems.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId
                        && x.VaultId == vaultId
                        && x.RotationId == rotationId
                        && x.Kind == kind
                        && subjectIds.Contains(x.SubjectId))
            .ToListAsync(cancellationToken);

    private static void AddBounded<T>(List<T> values, T value, ref bool hasMoreIssues)
    {
        if (values.Count < MaximumReportedIssuesPerCategory)
        {
            values.Add(value);
        }
        else
        {
            hasMoreIssues = true;
        }
    }

    private static RotationCoverage InspectCoverage(
        Domain.Vault vault,
        VaultKeyRotation rotation,
        IReadOnlyCollection<VaultKeyRotationPreparedItem> items,
        MemberCoverage memberCoverage,
        AgentCoverage agentCoverage,
        FullGrantCoverage fullGrantCoverage,
        EntryCoverage entryCoverage)
    {
        var metadataItem = items.SingleOrDefault(x => x.Kind == VaultKeyRotationPreparedItemKind.VaultMetadata
            && x.SubjectId == vault.Id);
        var metadataRequired = rotation.Scope.HasFlag(VaultKeyRotationScope.VaultKey);
        var metadataDirty = metadataRequired
            ? metadataItem is null || metadataItem.SourceRevision != vault.MetadataRevision.Value
            : metadataItem is not null;
        var requiredKeyMaterial = RequiredKeyMaterialKinds(rotation.Scope);
        var currentKeyMaterial = vault.KeyMaterialEnvelopes.ToDictionary(x => x.Kind);
        var preparedKeyMaterial = items
            .Where(x => x.Kind == VaultKeyRotationPreparedItemKind.VaultKeyMaterial)
            .ToDictionary(x => (VaultKeyMaterialKind)x.SubjectVersion);
        var preparedTrustAnchors = items
            .Where(x => x.Kind == VaultKeyRotationPreparedItemKind.VaultPublicTrustAnchor)
            .Select(x => (VaultPublicKeyKindContract)x.SubjectVersion)
            .ToHashSet();
        var requiredTrustAnchors = RequiredTrustAnchorKinds(rotation.Scope);
        var missingKeyMaterial = requiredKeyMaterial
            .Where(kind => !preparedKeyMaterial.ContainsKey(kind))
            .Select(x => (ushort)x)
            .ToArray();
        var dirtyKeyMaterial = requiredKeyMaterial
            .Where(kind => preparedKeyMaterial.TryGetValue(kind, out var item)
                           && (!currentKeyMaterial.TryGetValue(kind, out var current)
                               || item.SourceRevision != current.Revision))
            .Select(x => (ushort)x)
            .ToArray();
        var unexpectedKeyMaterial = preparedKeyMaterial.Keys.Except(requiredKeyMaterial).Any();
        var response = new VaultKeyRotationIncompleteResponse(
            memberCoverage.MissingIds,
            agentCoverage.MissingIds,
            fullGrantCoverage.MissingIds,
            memberCoverage.DirtyIds,
            agentCoverage.DirtyIds,
            fullGrantCoverage.DirtyIds,
            entryCoverage.MissingKeys,
            entryCoverage.MissingDiscoveryIds,
            entryCoverage.DirtyKeys,
            entryCoverage.DirtyDiscoveryIds,
            metadataDirty,
            missingKeyMaterial,
            dirtyKeyMaterial,
            entryCoverage.HasMoreIssues
            || memberCoverage.HasMoreIssues
            || agentCoverage.HasMoreIssues
            || fullGrantCoverage.HasMoreIssues);
        return new RotationCoverage(items.Count == (metadataRequired ? 1 : 0) + requiredKeyMaterial.Length
            + requiredTrustAnchors.Length
            && memberCoverage.IsComplete
            && agentCoverage.IsComplete
            && fullGrantCoverage.IsComplete
            && entryCoverage.IsComplete
            && response.DirtyMemberIds.Length == 0
            && response.DirtyAgentIds.Length == 0
            && response.DirtyFullGrantIds.Length == 0
            && !response.VaultMetadataDirty
            && response.MissingKeyMaterialKinds.Length == 0
            && response.DirtyKeyMaterialKinds.Length == 0
            && requiredTrustAnchors.All(preparedTrustAnchors.Contains)
            && preparedTrustAnchors.SetEquals(requiredTrustAnchors)
            && !unexpectedKeyMaterial, response);
    }

    private static VaultKeyMaterialKind[] RequiredKeyMaterialKinds(VaultKeyRotationScope scope)
    {
        if (scope.HasFlag(VaultKeyRotationScope.VaultKey))
        {
            return Enum.GetValues<VaultKeyMaterialKind>();
        }

        var kinds = new List<VaultKeyMaterialKind>(3);
        if (scope.HasFlag(VaultKeyRotationScope.Vdk)) kinds.Add(VaultKeyMaterialKind.DiscoveryKey);
        if (scope.HasFlag(VaultKeyRotationScope.AgentMessage)) kinds.Add(VaultKeyMaterialKind.AgentMessagePrivateKey);
        if (scope.HasFlag(VaultKeyRotationScope.ManifestSigning)) kinds.Add(VaultKeyMaterialKind.ManifestSigningPrivateKey);
        return kinds.ToArray();
    }

    private static VaultPublicKeyKindContract[] RequiredTrustAnchorKinds(VaultKeyRotationScope scope) =>
        new[]
        {
            scope.HasFlag(VaultKeyRotationScope.AgentMessage)
                ? VaultPublicKeyKindContract.AgentMessageX25519
                : (VaultPublicKeyKindContract?)null,
            scope.HasFlag(VaultKeyRotationScope.ManifestSigning)
                ? VaultPublicKeyKindContract.ManifestSigningEd25519
                : null,
        }.OfType<VaultPublicKeyKindContract>().ToArray();

    private static VaultKeyMaterialEnvelope DecodeKeyMaterial(VaultKeyRotationPreparedItem item) =>
        (VaultKeyMaterialKind)item.SubjectVersion switch
        {
            VaultKeyMaterialKind.DiscoveryKey => VaultEnvelopeContractMapper.ToDomain(
                VaultPreparedPayloadCodec.Decode<VaultDiscoveryKeyEnvelopeContract>(item.Payload)),
            VaultKeyMaterialKind.AgentMessagePrivateKey or VaultKeyMaterialKind.ManifestSigningPrivateKey =>
                VaultEnvelopeContractMapper.ToDomain(
                    VaultPreparedPayloadCodec.Decode<VaultPrivateKeyEnvelopeContract>(item.Payload)),
            _ => throw new DomainException("Unknown prepared Vault key material kind."),
        };

    private static T DecodeSingle<T>(
        IReadOnlyCollection<VaultKeyRotationPreparedItem> items,
        VaultKeyRotationPreparedItemKind kind,
        Guid subjectId) =>
        VaultPreparedPayloadCodec.Decode<T>(items.Single(x => x.Kind == kind && x.SubjectId == subjectId).Payload);

    private sealed record RotationCoverage(bool IsComplete, VaultKeyRotationIncompleteResponse Response);
    private sealed record MemberCoverage(
        bool IsComplete,
        Guid[] MissingIds,
        Guid[] DirtyIds,
        bool HasMoreIssues)
    {
        internal static readonly MemberCoverage Complete = new(true, [], [], false);
    }
    private sealed record AgentCoverage(
        bool IsComplete,
        Guid[] MissingIds,
        Guid[] DirtyIds,
        bool HasMoreIssues)
    {
        internal static readonly AgentCoverage Complete = new(true, [], [], false);
    }
    private sealed record FullGrantCoverage(
        bool IsComplete,
        Guid[] MissingIds,
        Guid[] DirtyIds,
        bool HasMoreIssues)
    {
        internal static readonly FullGrantCoverage Complete = new(true, [], [], false);
    }
    private sealed record EntryCoverage(
        bool IsComplete,
        RotationEntryKeyIdentity[] MissingKeys,
        Guid[] MissingDiscoveryIds,
        RotationEntryKeyIdentity[] DirtyKeys,
        Guid[] DirtyDiscoveryIds,
        bool HasMoreIssues)
    {
        internal static readonly EntryCoverage Complete = new(true, [], [], [], [], false);
    }
}
