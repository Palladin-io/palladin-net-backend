using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Api;
using Palladin.Core.Security;
using Palladin.Core.Types.Exceptions;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Authorization;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record RotationAgentDiscoveryContract(
    Guid AgentId,
    AgentVaultDiscoveryEnvelopeContract Envelope,
    VaultManifestContract Manifest);

[PublicAPI]
public sealed record RotationEntryDiscoveryContract(
    string SourceRevision,
    AgentDiscoveryEnvelopeContract Envelope);

[PublicAPI]
public sealed record PrepareVaultKeyRotationBatchRequest : IRequiresVaultMembership
{
    public Guid VaultId { get; init; }
    public Guid RotationId { get; init; }
    public Guid FencingToken { get; init; }
    public MemberVaultMetadataEnvelopeContract? MemberVaultMetadata { get; init; }
    public IReadOnlyList<MemberVaultKeyEnvelopeContract> MemberVaultKeys { get; init; } = [];
    public IReadOnlyList<VaultEntryKeyContract> EntryKeys { get; init; } = [];
    public IReadOnlyList<RotationEntryDiscoveryContract> EntryDiscoveries { get; init; } = [];
    public IReadOnlyList<RotationAgentDiscoveryContract> AgentDiscoveries { get; init; } = [];
    public VaultDiscoveryKeyEnvelopeContract? DiscoveryKey { get; init; }
    public IReadOnlyList<VaultPrivateKeyEnvelopeContract> VaultPrivateKeys { get; init; } = [];
    public VaultPublicKeyContract? VaultAgentMessagePublicKey { get; init; }
    public VaultPublicKeyContract? VaultManifestSigningPublicKey { get; init; }
}

[PublicAPI]
public sealed record PrepareVaultKeyRotationBatchResponse(int AcceptedItems, int TotalPreparedItems);

[UsedImplicitly]
internal sealed class PrepareVaultKeyRotationBatchValidator : Validator<PrepareVaultKeyRotationBatchRequest>
{
    public PrepareVaultKeyRotationBatchValidator()
    {
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.RotationId).NotEmpty();
        RuleFor(x => x.FencingToken).NotEmpty();
        RuleFor(x => x.MemberVaultKeys).NotNull();
        RuleFor(x => x.EntryKeys).NotNull();
        RuleFor(x => x.EntryDiscoveries).NotNull();
        RuleFor(x => x.AgentDiscoveries).NotNull();
        When(x => x.DiscoveryKey is not null, () =>
            RuleFor(x => x.DiscoveryKey!).SetValidator(new VaultDiscoveryKeyContractValidator()));
        RuleFor(x => x.VaultPrivateKeys).NotNull();
        When(x => x.VaultAgentMessagePublicKey is not null, () =>
            RuleFor(x => x.VaultAgentMessagePublicKey!).NotNull());
        When(x => x.VaultManifestSigningPublicKey is not null, () =>
            RuleFor(x => x.VaultManifestSigningPublicKey!).NotNull());
        RuleForEach(x => x.VaultPrivateKeys).SetValidator(new VaultPrivateKeyContractValidator());
        When(x => x.MemberVaultMetadata is not null, () =>
            RuleFor(x => x.MemberVaultMetadata!).SetValidator(new MemberVaultMetadataContractValidator()));
        RuleForEach(x => x.MemberVaultKeys).SetValidator(new MemberVaultKeyContractValidator());
        RuleForEach(x => x.EntryKeys).ChildRules(item =>
        {
            item.RuleFor(x => x.OrganizationId).NotEmpty();
            item.RuleFor(x => x.VaultId).NotEmpty();
            item.RuleFor(x => x.EntryId).NotEmpty();
            item.RuleFor(x => x.WrapperRevision).NotEmpty().MaximumLength(20);
            item.RuleFor(x => x.Descriptor).NotNull();
            item.RuleFor(x => x.EncodedSuitePayload).NotEmpty().MaximumLength(118);
        });
        RuleForEach(x => x.EntryDiscoveries).ChildRules(item =>
        {
            item.RuleFor(x => x.SourceRevision).NotEmpty().MaximumLength(20);
            item.RuleFor(x => x.Envelope).NotNull();
        });
        RuleForEach(x => x.AgentDiscoveries).ChildRules(item =>
        {
            item.RuleFor(x => x.AgentId).NotEmpty();
            item.RuleFor(x => x.Envelope).NotNull();
            item.RuleFor(x => x.Manifest).NotNull();
        });
        RuleFor(x => x).Must(x =>
                (x.MemberVaultMetadata is null ? 0 : 1)
                + (x.MemberVaultKeys?.Count ?? 0)
                + (x.EntryKeys?.Count ?? 0)
                + (x.EntryDiscoveries?.Count ?? 0)
                + (x.AgentDiscoveries?.Count ?? 0)
                + (x.DiscoveryKey is null ? 0 : 1)
                + (x.VaultPrivateKeys?.Count ?? 0)
                + (x.VaultAgentMessagePublicKey is null ? 0 : 1)
                + (x.VaultManifestSigningPublicKey is null ? 0 : 1)
                is > 0 and <= VaultProtocol.MaximumRotationBatchItems);
    }
}

[PublicAPI]
internal sealed class PrepareVaultKeyRotationBatchEndpoint(
    VaultDomainWriteContext domainWriteContext,
    IClock clock) : Endpoint<PrepareVaultKeyRotationBatchRequest, PrepareVaultKeyRotationBatchResponse>
{
    public override void Configure()
    {
        Put("api/vaults/{vaultId:guid}/key-rotations/{rotationId:guid}/batch");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.VaultManage);
        this.RequireEmailVerified();
        this.RequireVaultMembership();
        Summary(summary =>
        {
            summary.Summary = "Upload a resumable bounded Vault key rotation batch";
            summary.Description = "Validates target versions and stores only pending encrypted material. Exact retries are idempotent and a current fencing token is mandatory.";
        });
        Tags("Vault/Key Rotation");
    }

    public override async Task HandleAsync(PrepareVaultKeyRotationBatchRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId()!.Value;
        var userId = User.GetUserId()!.Value;
        await using var transaction = await domainWriteContext.BeginTransactionAsync(ct);
        if (await domainWriteContext.LockVault(organizationId, req.VaultId).SingleOrDefaultAsync(ct) is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        var rotation = await domainWriteContext.VaultKeyRotations
            .Where(x => x.OrganizationId == organizationId && x.VaultId == req.VaultId && x.Id == req.RotationId)
            .SingleOrDefaultAsync(ct);
        if (rotation is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (rotation.ExcludedMemberId == userId)
        {
            AddError(ErrorResponses.General("rotation-removing-principal"));
            await Send.ErrorsAsync(409, ct);
            return;
        }

        var now = clock.GetCurrentInstant();
        rotation.AssertLease(userId, req.FencingToken, now);
        var rotatesVaultKey = rotation.Scope.HasFlag(VaultKeyRotationScope.VaultKey);
        var rotatesVdk = rotation.Scope.HasFlag(VaultKeyRotationScope.Vdk);
        var rotatesAgentProjection = rotatesVdk
                                     || rotation.Scope.HasFlag(VaultKeyRotationScope.AgentMessage)
                                     || rotation.Scope.HasFlag(VaultKeyRotationScope.ManifestSigning);
        if ((!rotatesVaultKey
             && (req.MemberVaultMetadata is not null || req.MemberVaultKeys.Count > 0 || req.EntryKeys.Count > 0))
            || (!rotatesVdk && req.EntryDiscoveries.Count > 0)
            || (!rotatesAgentProjection && req.AgentDiscoveries.Count > 0)
            || (req.DiscoveryKey is not null && !IsKeyMaterialInScope(VaultKeyMaterialKind.DiscoveryKey, rotation.Scope))
            || req.VaultPrivateKeys.Any(x => !IsKeyMaterialInScope(ToKeyMaterialKind(x.PrivateKeyKind), rotation.Scope)))
        {
            throw new DomainException("Prepared items exceed the requested Vault rotation scope.");
        }

        var requestedItems = GetRequestedItems(req);
        await domainWriteContext.RequestedRotationItems(
                organizationId,
                req.VaultId,
                req.RotationId,
                requestedItems.Select(x => (int)x.Kind).ToArray(),
                requestedItems.Select(x => x.SubjectId).ToArray(),
                requestedItems.Select(x => (decimal)x.SubjectVersion).ToArray())
            .ToListAsync(ct);
        domainWriteContext.EnsureRotationPreparedItemTrackingIsBounded(requestedItems.Length);
        var accepted = 0;
        var requestedKeyMaterial = (req.DiscoveryKey is null
                ? Enumerable.Empty<(VaultKeyMaterialEnvelope Envelope, object Contract)>()
                : new[] { (Envelope: VaultEnvelopeContractMapper.ToDomain(req.DiscoveryKey), Contract: (object)req.DiscoveryKey) })
            .Concat(req.VaultPrivateKeys.Select(x =>
                (Envelope: VaultEnvelopeContractMapper.ToDomain(x), Contract: (object)x)))
            .ToArray();
        if (requestedKeyMaterial.Length > 0)
        {
            var requestedKinds = requestedKeyMaterial.Select(x => x.Envelope.Kind).Distinct().ToArray();
            if (requestedKinds.Length != requestedKeyMaterial.Length)
            {
                throw new DomainException("A Vault key material kind may appear only once per batch.");
            }
            var current = await domainWriteContext.VaultKeyMaterialEnvelopes.AsNoTracking()
                .Where(x => x.OrganizationId == organizationId && x.VaultId == req.VaultId
                            && requestedKinds.Contains(x.Kind))
                .ToDictionaryAsync(x => x.Kind, ct);
            foreach (var (envelope, contract) in requestedKeyMaterial)
            {
                if (!current.TryGetValue(envelope.Kind, out var source)
                    || envelope.OrganizationId != organizationId
                    || envelope.VaultId != req.VaultId
                    || envelope.Revision != checked(source.Revision + 1)
                    || envelope.MemberKeyGeneration != rotation.TargetMemberKeyGeneration
                    || envelope.WrappingKeyVersion != rotation.TargetKeyEpoch.VaultKeyVersion
                    || envelope.KeyVersion != ExpectedKeyMaterialVersion(envelope.Kind, rotation.TargetKeyEpoch))
                {
                    throw new DomainException("Prepared Vault key material does not target the rotation key epoch.");
                }

                accepted += Prepare(rotation, VaultKeyRotationPreparedItemKind.VaultKeyMaterial, req.VaultId,
                    (ulong)envelope.Kind, source.Revision, contract, userId, req.FencingToken, now);
            }
        }
        accepted += PrepareTrustAnchor(req.VaultAgentMessagePublicKey,
            VaultPublicKeyKindContract.AgentMessageX25519,
            rotation.TargetKeyEpoch.AgentMessageKeyVersion.Value,
            VaultKeyRotationScope.AgentMessage);
        accepted += PrepareTrustAnchor(req.VaultManifestSigningPublicKey,
            VaultPublicKeyKindContract.ManifestSigningEd25519,
            rotation.TargetKeyEpoch.ManifestSigningKeyVersion.Value,
            VaultKeyRotationScope.ManifestSigning);
        if (req.MemberVaultMetadata is not null)
        {
            var metadata = VaultEnvelopeContractMapper.ToDomain(req.MemberVaultMetadata);
            if (metadata.Scope != new VaultScope(organizationId, req.VaultId)
                || metadata.Header.MemberKeyGeneration != rotation.TargetMemberKeyGeneration
                || metadata.Header.KeyVersion != rotation.TargetKeyEpoch.VaultKeyVersion)
            {
                throw new DomainException("Prepared Vault metadata does not target the rotation generation.");
            }

            accepted += Prepare(rotation, VaultKeyRotationPreparedItemKind.VaultMetadata, req.VaultId, 0,
                checked(metadata.MetadataRevision.Value - 1), req.MemberVaultMetadata, userId, req.FencingToken, now);
        }

        foreach (var contract in req.MemberVaultKeys)
        {
            var envelope = VaultEnvelopeContractMapper.ToDomain(contract);
            if (envelope.Scope != new VaultScope(organizationId, req.VaultId)
                || envelope.MemberId == rotation.ExcludedMemberId
                || envelope.MemberKeyGeneration != rotation.TargetMemberKeyGeneration
                || envelope.VaultKeyVersion != rotation.TargetKeyEpoch.VaultKeyVersion)
            {
                throw new DomainException("Prepared Member Vault key does not target the rotation generation.");
            }

            accepted += Prepare(rotation, VaultKeyRotationPreparedItemKind.MemberVaultKey, envelope.MemberId, 0, 0,
                contract, userId, req.FencingToken, now);
        }

        foreach (var contract in req.EntryKeys)
        {
            var entryKey = VaultEnvelopeContractMapper.ToDomain(contract);
            if (entryKey.OrganizationId != organizationId || entryKey.VaultId != req.VaultId
                || entryKey.MemberKeyGeneration != rotation.TargetMemberKeyGeneration
                || entryKey.WrappingKeyVersion != rotation.TargetKeyEpoch.VaultKeyVersion)
            {
                throw new DomainException("Prepared Entry key does not target the rotation generation.");
            }

            accepted += Prepare(rotation, VaultKeyRotationPreparedItemKind.EntryKey, entryKey.EntryId,
                entryKey.KeyVersion.Value, checked(entryKey.WrapperRevision.Value - 1), contract,
                userId, req.FencingToken, now);
        }

        foreach (var contract in req.EntryDiscoveries)
        {
            var discovery = VaultEnvelopeContractMapper.ToDomain(contract.Envelope);
            var sourceRevision = VaultEnvelopeContractMapper.ParseUInt64(contract.SourceRevision);
            if (discovery.Scope.OrganizationId != organizationId || discovery.Scope.VaultId != req.VaultId
                || discovery.VdkVersion != rotation.TargetKeyEpoch.VdkVersion
                || discovery.Header.MemberKeyGeneration != rotation.TargetMemberKeyGeneration
                || discovery.Revision.Value != sourceRevision)
            {
                throw new DomainException("Prepared Agent Discovery projection does not target the rotation generation.");
            }

            accepted += Prepare(rotation, VaultKeyRotationPreparedItemKind.EntryDiscovery, discovery.Scope.EntryId,
                0, sourceRevision, contract.Envelope, userId, req.FencingToken, now);
        }

        if (req.AgentDiscoveries.Count > 0)
        {
            var agentIds = req.AgentDiscoveries.Select(x => x.AgentId).Distinct().ToArray();
            var agents = await domainWriteContext.Agents
                .Where(x => x.OrganizationId == organizationId && agentIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, ct);
            foreach (var contract in req.AgentDiscoveries)
            {
                if (contract.AgentId == rotation.ExcludedAgentId
                    || !agents.TryGetValue(contract.AgentId, out var agent))
                {
                    throw new DomainException("Prepared Agent Discovery recipient is not an active organization Agent.");
                }

                var provisioning = VaultManifestCryptoValidator.Validate(contract.Envelope, contract.Manifest, agent);
                if (provisioning.Scope != new VaultScope(organizationId, req.VaultId)
                    || provisioning.VdkVersion != rotation.TargetKeyEpoch.VdkVersion
                    || provisioning.Manifest.AgentMessageKeyVersion != rotation.TargetKeyEpoch.AgentMessageKeyVersion
                    || provisioning.Manifest.ManifestSigningKeyVersion != rotation.TargetKeyEpoch.ManifestSigningKeyVersion)
                {
                    throw new DomainException("Prepared Agent manifest does not target the rotation key epoch.");
                }

                accepted += Prepare(rotation, VaultKeyRotationPreparedItemKind.AgentDiscoveryEnvelope,
                    contract.AgentId, 0, checked(provisioning.ManifestRevision.Value - 1), contract,
                    userId, req.FencingToken, now);
            }
        }

        await domainWriteContext.CommitAsync(ct);
        var totalPreparedItems = await domainWriteContext.VaultKeyRotationPreparedItems
            .AsNoTracking()
            .CountAsync(x => x.OrganizationId == organizationId
                             && x.VaultId == req.VaultId
                             && x.RotationId == req.RotationId, ct);
        await transaction.CommitAsync(ct);
        await Send.OkAsync(new PrepareVaultKeyRotationBatchResponse(accepted, totalPreparedItems), ct);

        int PrepareTrustAnchor(VaultPublicKeyContract? contract, VaultPublicKeyKindContract kind,
            uint targetVersion, VaultKeyRotationScope requiredScope)
        {
            if (contract is null)
            {
                return 0;
            }

            if (!rotation.Scope.HasFlag(requiredScope))
            {
                throw new DomainException("Prepared public trust anchor exceeds the requested Vault rotation scope.");
            }

            _ = VaultEnvelopeContractMapper.ToDomain(contract, kind, targetVersion);
            return Prepare(rotation, VaultKeyRotationPreparedItemKind.VaultPublicTrustAnchor, req.VaultId,
                (ulong)kind, 0, contract, userId, req.FencingToken, now);
        }
    }

    private static PreparedItemIdentity[] GetRequestedItems(PrepareVaultKeyRotationBatchRequest request) =>
        (request.MemberVaultMetadata is null
            ? Array.Empty<PreparedItemIdentity>()
            : [new(VaultKeyRotationPreparedItemKind.VaultMetadata, request.VaultId, 0)])
        .Concat(request.MemberVaultKeys.Select(x =>
            new PreparedItemIdentity(VaultKeyRotationPreparedItemKind.MemberVaultKey, x.MemberId, 0)))
        .Concat(request.EntryKeys.Select(x =>
            new PreparedItemIdentity(VaultKeyRotationPreparedItemKind.EntryKey, x.EntryId, x.KeyVersion)))
        .Concat(request.EntryDiscoveries.Select(x =>
            new PreparedItemIdentity(VaultKeyRotationPreparedItemKind.EntryDiscovery, x.Envelope.EntryId, 0)))
        .Concat(request.AgentDiscoveries.Select(x =>
            new PreparedItemIdentity(VaultKeyRotationPreparedItemKind.AgentDiscoveryEnvelope, x.AgentId, 0)))
        .Concat(request.DiscoveryKey is null
            ? []
            : new[] { new PreparedItemIdentity(VaultKeyRotationPreparedItemKind.VaultKeyMaterial,
                request.VaultId, (ulong)VaultKeyMaterialKind.DiscoveryKey) })
        .Concat(request.VaultPrivateKeys.Select(x =>
            new PreparedItemIdentity(VaultKeyRotationPreparedItemKind.VaultKeyMaterial,
                request.VaultId, (ulong)ToKeyMaterialKind(x.PrivateKeyKind))))
        .Concat(request.VaultAgentMessagePublicKey is null
            ? []
            : new[] { new PreparedItemIdentity(VaultKeyRotationPreparedItemKind.VaultPublicTrustAnchor,
                request.VaultId, (ulong)VaultPublicKeyKindContract.AgentMessageX25519) })
        .Concat(request.VaultManifestSigningPublicKey is null
            ? []
            : new[] { new PreparedItemIdentity(VaultKeyRotationPreparedItemKind.VaultPublicTrustAnchor,
                request.VaultId, (ulong)VaultPublicKeyKindContract.ManifestSigningEd25519) })
        .Distinct()
        .ToArray();

    private static bool IsKeyMaterialInScope(VaultKeyMaterialKind kind, VaultKeyRotationScope scope) =>
        scope.HasFlag(VaultKeyRotationScope.VaultKey)
        || kind switch
        {
            VaultKeyMaterialKind.DiscoveryKey => scope.HasFlag(VaultKeyRotationScope.Vdk),
            VaultKeyMaterialKind.AgentMessagePrivateKey => scope.HasFlag(VaultKeyRotationScope.AgentMessage),
            VaultKeyMaterialKind.ManifestSigningPrivateKey => scope.HasFlag(VaultKeyRotationScope.ManifestSigning),
            _ => false,
        };

    private static VaultKeyMaterialKind ToKeyMaterialKind(ushort privateKeyKind) => privateKeyKind switch
    {
        1 => VaultKeyMaterialKind.AgentMessagePrivateKey,
        2 => VaultKeyMaterialKind.ManifestSigningPrivateKey,
        _ => throw new DomainException("Unknown Vault private key kind."),
    };

    private static uint ExpectedKeyMaterialVersion(VaultKeyMaterialKind kind, VaultKeyEpoch epoch) => kind switch
    {
        VaultKeyMaterialKind.DiscoveryKey => epoch.VdkVersion.Value,
        VaultKeyMaterialKind.AgentMessagePrivateKey => epoch.AgentMessageKeyVersion.Value,
        VaultKeyMaterialKind.ManifestSigningPrivateKey => epoch.ManifestSigningKeyVersion.Value,
        _ => throw new DomainException("Unknown Vault key material kind."),
    };

    private static int Prepare<T>(
        VaultKeyRotation rotation,
        VaultKeyRotationPreparedItemKind kind,
        Guid subjectId,
        ulong subjectVersion,
        ulong sourceRevision,
        T payload,
        Guid memberId,
        Guid fencingToken,
        Instant now) => rotation.Prepare(
        VaultKeyRotationPreparedItem.Create(rotation, kind, subjectId, subjectVersion, sourceRevision,
            VaultKeyRotationPayloadCodec.Encode(payload), now), memberId, fencingToken, now) ? 1 : 0;

    private sealed record PreparedItemIdentity(
        VaultKeyRotationPreparedItemKind Kind,
        Guid SubjectId,
        ulong SubjectVersion);
}
