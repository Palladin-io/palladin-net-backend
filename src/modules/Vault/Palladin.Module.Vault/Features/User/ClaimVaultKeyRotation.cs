using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Api;
using Palladin.Core.Guid;
using Palladin.Core.Security;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Authorization;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record ClaimVaultKeyRotationRequest : IRequiresVaultMembership
{
    public Guid VaultId { get; init; }
    public Guid RotationId { get; init; }
}

[PublicAPI]
public sealed record ClaimVaultKeyRotationResponse(
    Guid OrganizationId,
    VaultKeyRotationResponse Rotation,
    Guid FencingToken,
    MemberVaultKeyEnvelopeContract CurrentMemberVaultKey,
    VaultDiscoveryKeyEnvelopeContract CurrentDiscoveryKey,
    IReadOnlyList<VaultPrivateKeyEnvelopeContract> CurrentVaultPrivateKeys,
    MemberVaultKeyEnvelopeContract? PendingMemberVaultKey,
    VaultDiscoveryKeyEnvelopeContract? PendingDiscoveryKey,
    IReadOnlyList<VaultPrivateKeyEnvelopeContract> PendingVaultPrivateKeys,
    bool PreparedMaterialReset);

[UsedImplicitly]
internal sealed class ClaimVaultKeyRotationValidator : Validator<ClaimVaultKeyRotationRequest>
{
    public ClaimVaultKeyRotationValidator()
    {
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.RotationId).NotEmpty();
    }
}

[PublicAPI]
internal sealed class ClaimVaultKeyRotationEndpoint(
    VaultDomainWriteContext domainWriteContext,
    IGuidProvider guidProvider,
    IClock clock) : Endpoint<ClaimVaultKeyRotationRequest, ClaimVaultKeyRotationResponse>
{
    public override void Configure()
    {
        Post("api/vaults/{vaultId:guid}/key-rotations/{rotationId:guid}/claim");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.VaultManage);
        this.RequireEmailVerified();
        this.RequireVaultMembership();
        Summary(summary =>
        {
            summary.Summary = "Claim a bounded Vault key rotation preparation lease";
            summary.Description = "Issues a new fencing token to the remaining Vault Member. An unexpired lease held by another Member fails closed; an expired lease is safely resumable.";
        });
        Tags("Vault/Key Rotation");
    }

    public override async Task HandleAsync(ClaimVaultKeyRotationRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId()!.Value;
        var userId = User.GetUserId()!.Value;
        var vault = await domainWriteContext.Vaults
            .Where(x => x.OrganizationId == organizationId && x.Id == req.VaultId)
            .SingleOrDefaultAsync(ct);
        if (vault is null
            || !await domainWriteContext.VaultMembers.AnyAsync(
                x => x.OrganizationId == organizationId
                     && x.VaultId == req.VaultId
                     && x.UserId == userId,
                ct))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var rotation = await domainWriteContext.VaultKeyRotations.SingleOrDefaultAsync(
            x => x.OrganizationId == organizationId
                 && x.VaultId == req.VaultId
                 && x.Id == req.RotationId,
            ct);
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

        var fencingToken = guidProvider.Generate();
        rotation.Claim(
            userId,
            fencingToken,
            clock.GetCurrentInstant(),
            Duration.FromSeconds(VaultProtocol.RotationLeaseSeconds));
        var claimedLeaseRevision = rotation.LeaseRevision;
        var currentMemberKey = await domainWriteContext.VaultMemberKeyEnvelopes.AsNoTracking().SingleAsync(
            x => x.OrganizationId == organizationId
                 && x.VaultId == req.VaultId
                 && x.MemberId == userId
                 && x.MemberKeyGeneration == vault.MemberKeyGeneration,
            ct);
        var currentKeyMaterial = await domainWriteContext.VaultKeyMaterialEnvelopes.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.VaultId == req.VaultId)
            .OrderBy(x => x.Kind)
            .ToListAsync(ct);
        var recoveryItems = await domainWriteContext.VaultKeyRotationPreparedItems.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId
                        && x.VaultId == req.VaultId
                        && x.RotationId == req.RotationId
                        && ((x.Kind == VaultKeyRotationPreparedItemKind.MemberVaultKey && x.SubjectId == userId)
                            || x.Kind == VaultKeyRotationPreparedItemKind.VaultKeyMaterial))
            .ToListAsync(ct);
        var pendingMemberItem = recoveryItems.SingleOrDefault(x =>
            x.Kind == VaultKeyRotationPreparedItemKind.MemberVaultKey && x.SubjectId == userId);
        var pendingKeyItems = recoveryItems
            .Where(x => x.Kind == VaultKeyRotationPreparedItemKind.VaultKeyMaterial)
            .ToArray();
        var canResume = (!rotation.Scope.HasFlag(VaultKeyRotationScope.VaultKey) || pendingMemberItem is not null)
                        && pendingKeyItems.Length == rotation.RequiredKeyMaterialKinds.Length
                        && pendingKeyItems.Select(x => (VaultKeyMaterialKind)x.SubjectVersion)
                            .Order().SequenceEqual(rotation.RequiredKeyMaterialKinds.Order());
        var preparedMaterialReset = false;
        if (!canResume)
        {
            pendingMemberItem = null;
            pendingKeyItems = [];
            preparedMaterialReset = true;
        }
        // LeaseRevision is an optimistic token: concurrent claim/prepare/commit attempts cannot
        // all succeed, while the ordinary SaveChanges keeps this transition short.
        await domainWriteContext.CommitAsync(ct);
        if (preparedMaterialReset)
        {
            domainWriteContext.Clear();
            var resetByCurrentLease = await domainWriteContext.ResetVaultKeyRotationPreparedItemsIfLeaseCurrentAsync(
                organizationId,
                req.VaultId,
                req.RotationId,
                fencingToken,
                claimedLeaseRevision,
                ct);
            if (!resetByCurrentLease)
            {
                throw new VaultKeyRotationFenceException();
            }
        }
        await Send.OkAsync(new ClaimVaultKeyRotationResponse(
            organizationId,
            VaultKeyRotationResponses.Map(rotation),
            fencingToken,
            VaultEnvelopeContractMapper.ToContract(currentMemberKey.GetWrappedVaultKey()),
            VaultEnvelopeContractMapper.ToDiscoveryKeyContract(
                currentKeyMaterial.Single(x => x.Kind == VaultKeyMaterialKind.DiscoveryKey)),
            currentKeyMaterial
                .Where(x => x.Kind != VaultKeyMaterialKind.DiscoveryKey)
                .Select(VaultEnvelopeContractMapper.ToPrivateKeyContract)
                .ToArray(),
            pendingMemberItem is null ? null : VaultPreparedPayloadCodec.Decode<MemberVaultKeyEnvelopeContract>(pendingMemberItem.Payload),
            pendingKeyItems.SingleOrDefault(x =>
                    x.SubjectVersion == (ulong)VaultKeyMaterialKind.DiscoveryKey) is { } pendingDiscovery
                ? VaultPreparedPayloadCodec.Decode<VaultDiscoveryKeyEnvelopeContract>(pendingDiscovery.Payload)
                : null,
            pendingKeyItems
                .Where(x => x.SubjectVersion != (ulong)VaultKeyMaterialKind.DiscoveryKey)
                .Select(x => VaultPreparedPayloadCodec.Decode<VaultPrivateKeyEnvelopeContract>(x.Payload))
                .ToArray(),
            preparedMaterialReset), ct);
    }
}
