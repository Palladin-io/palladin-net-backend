using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Palladin.Core.Security;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Persistence;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record ListPendingVaultKeyRotationsResponse(IReadOnlyList<VaultKeyRotationResponse> Items);

[PublicAPI]
internal sealed class ListPendingVaultKeyRotationsEndpoint(
    VaultDomainReadContext domainReadContext)
    : EndpointWithoutRequest<ListPendingVaultKeyRotationsResponse>
{
    public override void Configure()
    {
        Get("api/vault-key-rotations/pending");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.VaultManage);
        this.RequireEmailVerified();
        Summary(summary =>
        {
            summary.Summary = "List pending Vault key rotations claimable after unlock";
            summary.Description = "Returns structural progress and target versions only for Vaults where the caller remains a Member. No key or ciphertext material is included.";
        });
        Tags("Vault/Key Rotation");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId()!.Value;
        var userId = User.GetUserId()!.Value;
        var rotations = await domainReadContext.VaultKeyRotations
            .Where(x => x.OrganizationId == organizationId)
            .Where(x => x.Status != VaultKeyRotationStatus.Committed)
            .Where(x => domainReadContext.VaultMembers.Any(member =>
                member.OrganizationId == x.OrganizationId
                && member.VaultId == x.VaultId
                && member.UserId == userId))
            .OrderBy(x => x.TriggeredAt)
            .ThenBy(x => x.Id)
            .ToListAsync(ct);

        await Send.OkAsync(new ListPendingVaultKeyRotationsResponse(
            rotations.Select(VaultKeyRotationResponses.Map).ToArray()), ct);
    }
}
