using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Security;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Authorization;
using Palladin.Module.Vault.Infrastructure.Persistence;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record ListVaultMembersRequest : IRequiresVaultMembership
{
    public Guid VaultId { get; init; }
    public Guid? AfterId { get; init; }
    public int PageSize { get; init; } = 50;
}

[UsedImplicitly]
internal sealed class ListVaultMembersValidator : Validator<ListVaultMembersRequest>
{
    public ListVaultMembersValidator()
    {
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
    }
}

[PublicAPI]
public sealed record VaultMemberDirectoryItem(
    Guid MemberId,
    string? MemberName,
    Instant AddedAt,
    string DeprovisioningStatus,
    Guid? RotationId);

[PublicAPI]
public sealed record ListVaultMembersResponse(
    IReadOnlyList<VaultMemberDirectoryItem> Items,
    Guid? NextAfterId);

[PublicAPI]
internal sealed class ListVaultMembersEndpoint(VaultDomainReadContext domainReadContext)
    : Endpoint<ListVaultMembersRequest, ListVaultMembersResponse>
{
    public override void Configure()
    {
        Get("api/vaults/{vaultId:guid}/members");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.VaultManage);
        this.RequireEmailVerified();
        this.RequireVaultMembership();
        Summary(summary =>
        {
            summary.Summary = "List structural Vault Members";
            summary.Description = "Returns a bounded opaque Member directory with staged deprovisioning state. Identity presentation data and key material are never included.";
        });
        Tags("Vault/Members");
    }

    public override async Task HandleAsync(ListVaultMembersRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId()!.Value;
        var members = domainReadContext.VaultMembers.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.VaultId == req.VaultId);
        if (req.AfterId is { } afterId)
        {
            members = members.Where(x => x.UserId.CompareTo(afterId) > 0);
        }

        var query =
            from member in members
            join user in domainReadContext.Users.AsNoTracking() on member.UserId equals user.Id into users
            from user in users.DefaultIfEmpty()
            select new { member.UserId, member.AddedAt, MemberName = (string?)user.DisplayName };
        var window = await query
            .OrderBy(x => x.UserId)
            .Take(req.PageSize + 1)
            .ToListAsync(ct);
        var page = window.Take(req.PageSize).ToArray();
        var memberIds = page.Select(x => x.UserId).ToArray();
        var operations = await domainReadContext.VaultPrincipalDeprovisionings.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId
                        && x.PrincipalType == VaultPrincipalType.OrganizationMember
                        && memberIds.Contains(x.PrincipalId)
                        && x.Status != VaultPrincipalDeprovisioningStatus.Completed)
            .Select(x => new
            {
                x.PrincipalId,
                x.Status,
                x.CurrentVaultId,
                x.CurrentRotationId,
            })
            .ToDictionaryAsync(x => x.PrincipalId, ct);

        var items = page.Select(member =>
        {
            operations.TryGetValue(member.UserId, out var operation);
            return new VaultMemberDirectoryItem(
                member.UserId,
                member.MemberName,
                member.AddedAt,
                MapStatus(operation?.Status, operation?.CurrentVaultId, req.VaultId),
                operation?.CurrentVaultId == req.VaultId ? operation.CurrentRotationId : null);
        }).ToArray();

        await Send.OkAsync(new ListVaultMembersResponse(
            items,
            window.Count > req.PageSize ? page[^1].UserId : null), ct);
    }

    private static string MapStatus(
        VaultPrincipalDeprovisioningStatus? status,
        Guid? currentVaultId,
        Guid vaultId) => status switch
    {
        null => "Active",
        VaultPrincipalDeprovisioningStatus.WaitingForRotation when currentVaultId == vaultId
            => "WaitingForRotation",
        VaultPrincipalDeprovisioningStatus.BlockedLastMember when currentVaultId == vaultId
            => "BlockedLastMember",
        _ => "Pending",
    };
}
