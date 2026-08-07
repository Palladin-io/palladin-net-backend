using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;
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
    Instant TriggeredAt,
    Instant? CommittedAt,
    string? LastFailureCode);

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
        await using var transaction = await domainWriteContext.BeginTransactionAsync(ct);
        var vault = await domainWriteContext.LockVault(organizationId, req.VaultId)
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
            await domainWriteContext.CommitAsync(ct);
        }

        await transaction.CommitAsync(ct);
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
        rotation.TriggeredAt,
        rotation.CommittedAt,
        rotation.LastFailureCode);

    private static VaultKeyEpochContract Map(VaultKeyEpoch epoch) => new(
        epoch.VaultKeyVersion.Value,
        epoch.VdkVersion.Value,
        epoch.AgentMessageKeyVersion.Value,
        epoch.ManifestSigningKeyVersion.Value);
}
