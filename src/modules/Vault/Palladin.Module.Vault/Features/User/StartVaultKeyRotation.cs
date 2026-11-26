using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;
using Palladin.Core.Guid;
using Palladin.Core.Security;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Authorization;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record StartVaultKeyRotationRequest : IRequiresVaultMembership
{
    public Guid VaultId { get; init; }
}

[PublicAPI]
public sealed record VaultKeyRotationResponse(
    Guid Id,
    Guid VaultId,
    string Status,
    string Cause,
    string[] Scope,
    uint BaseMemberKeyGeneration,
    uint TargetMemberKeyGeneration,
    VaultKeyEpochContract BaseKeyEpoch,
    VaultKeyEpochContract TargetKeyEpoch,
    string BaseMemberSequence,
    string BaseDiscoverySequence,
    ulong LeaseRevision,
    Guid? LeaseOwnerId,
    Instant? LeaseExpiresAt,
    Guid TriggeredBy,
    Instant TriggeredAt,
    Guid? CommittedBy,
    Instant? CommittedAt,
    string? LastFailureCode,
    IReadOnlyList<VaultKeyRotationReasonResponse> Reasons);

[PublicAPI]
public sealed record VaultKeyRotationReasonResponse(
    string PrincipalType,
    Guid PrincipalId,
    string? PrincipalName,
    Instant OccurredAt);

[PublicAPI]
internal sealed class StartVaultKeyRotationEndpoint(
    VaultDomainWriteContext domainWriteContext,
    IGuidProvider guidProvider,
    IClock clock) : Endpoint<StartVaultKeyRotationRequest, VaultKeyRotationResponse>
{
    public override void Configure()
    {
        Post("api/vaults/{vaultId:guid}/key-rotations");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.VaultManage);
        this.RequireEmailVerified();
        this.RequireVaultMembership();
        Summary(summary =>
        {
            summary.Summary = "Start a staged Vault security rotation";
            summary.Description = "Creates an idempotent full planned rotation requirement. Current key material remains authoritative until a complete prepared generation commits atomically.";
        });
        Tags("Vault/Key Rotation");
    }

    public override async Task HandleAsync(StartVaultKeyRotationRequest req, CancellationToken ct)
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

        var active = await domainWriteContext.VaultKeyRotations
            .Include(x => x.Reasons)
            .SingleOrDefaultAsync(
                x => x.OrganizationId == organizationId
                     && x.VaultId == req.VaultId
                     && x.Status != VaultKeyRotationStatus.Committed,
                ct);
        if (active is null)
        {
            active = VaultKeyRotation.Create(
                guidProvider.Generate(),
                vault,
                VaultKeyRotationCause.ManualSecurityRotation,
                VaultKeyRotationScope.VaultKey
                | VaultKeyRotationScope.Vdk
                | VaultKeyRotationScope.AgentMessage
                | VaultKeyRotationScope.ManifestSigning,
                userId,
                clock.GetCurrentInstant());
            domainWriteContext.Add(active);
            try
            {
                await domainWriteContext.CommitAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException
                   {
                       SqlState: Palladin.Core.Persistence.PostgresErrorCodes.UniqueViolation,
                       ConstraintName: "IX_VaultKeyRotations_OrganizationId_VaultId",
                   })
            {
                // The filtered unique index is the authoritative active-rotation invariant.
                domainWriteContext.Clear();
                active = await domainWriteContext.VaultKeyRotations.Include(x => x.Reasons).SingleAsync(
                    x => x.OrganizationId == organizationId
                         && x.VaultId == req.VaultId
                         && x.Status != VaultKeyRotationStatus.Committed,
                    ct);
            }
        }
        await Send.OkAsync(VaultKeyRotationResponses.Map(active), ct);
    }
}

internal static class VaultKeyRotationResponses
{
    internal static VaultKeyRotationResponse Map(VaultKeyRotation rotation) => new(
        rotation.Id,
        rotation.VaultId,
        rotation.Status.ToString(),
        rotation.Cause.ToString(),
        Enum.GetValues<VaultKeyRotationScope>()
            .Where(x => x != VaultKeyRotationScope.None && rotation.Scope.HasFlag(x))
            .Select(x => x.ToString())
            .ToArray(),
        rotation.BaseMemberKeyGeneration.Value,
        rotation.TargetMemberKeyGeneration.Value,
        Map(rotation.BaseKeyEpoch),
        Map(rotation.TargetKeyEpoch),
        rotation.BaseMemberSequence.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        rotation.BaseDiscoverySequence.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        rotation.LeaseRevision,
        rotation.LeaseOwnerId,
        rotation.LeaseExpiresAt,
        rotation.TriggeredBy,
        rotation.TriggeredAt,
        rotation.CommittedBy,
        rotation.CommittedAt,
        rotation.LastFailureCode,
        rotation.Reasons
            .OrderBy(x => x.OccurredAt)
            .ThenBy(x => x.PrincipalId)
            .Select(x => new VaultKeyRotationReasonResponse(
                x.PrincipalType.ToString(), x.PrincipalId, x.PrincipalName, x.OccurredAt))
            .ToArray());

    private static VaultKeyEpochContract Map(VaultKeyEpoch epoch) => new(
        epoch.VaultKeyVersion.Value,
        epoch.VdkVersion.Value,
        epoch.AgentMessageKeyVersion.Value,
        epoch.ManifestSigningKeyVersion.Value);
}
