using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Security;
using Palladin.Module.Vault.Infrastructure.Authorization;
using Palladin.Module.Vault.Infrastructure.Persistence;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record ListVaultKeyRotationsRequest : IRequiresVaultMembership
{
    public Guid VaultId { get; init; }
    public int PageSize { get; init; } = 50;
    public Instant? BeforeTriggeredAt { get; init; }
    public Guid? BeforeId { get; init; }
}

[UsedImplicitly]
internal sealed class ListVaultKeyRotationsValidator : Validator<ListVaultKeyRotationsRequest>
{
    public ListVaultKeyRotationsValidator()
    {
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
        RuleFor(x => x.BeforeId).NotNull().When(x => x.BeforeTriggeredAt is not null);
        RuleFor(x => x.BeforeTriggeredAt).NotNull().When(x => x.BeforeId is not null);
    }
}

[PublicAPI]
public sealed record ListVaultKeyRotationsResponse(
    Guid VaultCreatedBy,
    Instant VaultCreatedAt,
    IReadOnlyList<VaultKeyRotationResponse> Items,
    bool HasMore);

[PublicAPI]
internal sealed class ListVaultKeyRotationsEndpoint(VaultDomainReadContext domainReadContext)
    : Endpoint<ListVaultKeyRotationsRequest, ListVaultKeyRotationsResponse>
{
    public override void Configure()
    {
        Get("api/vaults/{vaultId:guid}/key-rotations");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.VaultManage);
        this.RequireEmailVerified();
        this.RequireVaultMembership();
        Summary(summary =>
        {
            summary.Summary = "List Vault key rotation history";
            summary.Description = "Returns rotations of one Vault, newest first, with their reasons, opaque actor IDs and target key versions. No key or ciphertext material is included.";
        });
        Tags("Vault/Key Rotation");
    }

    public override async Task HandleAsync(ListVaultKeyRotationsRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId()!.Value;
        var vault = await domainReadContext.Vaults
            .Where(x => x.OrganizationId == organizationId && x.Id == req.VaultId)
            .Select(x => new { x.CreatedBy, x.CreatedAt })
            .SingleAsync(ct);
        var rotations = domainReadContext.VaultKeyRotations
            .Where(x => x.OrganizationId == organizationId && x.VaultId == req.VaultId);
        if (req is { BeforeTriggeredAt: { } beforeAt, BeforeId: { } beforeId })
        {
            rotations = rotations.Where(x => x.TriggeredAt < beforeAt
                                             || (x.TriggeredAt == beforeAt && x.Id.CompareTo(beforeId) < 0));
        }

        var window = await rotations
            .Include(x => x.Reasons)
            .OrderByDescending(x => x.TriggeredAt)
            .ThenByDescending(x => x.Id)
            .Take(req.PageSize + 1)
            .ToListAsync(ct);

        await Send.OkAsync(new ListVaultKeyRotationsResponse(
            vault.CreatedBy,
            vault.CreatedAt,
            window.Take(req.PageSize).Select(VaultKeyRotationResponses.Map).ToArray(),
            window.Count > req.PageSize), ct);
    }
}
