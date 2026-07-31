using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record SearchOrganizationEntriesRequest
{
    public int? Limit { get; init; }
}

[PublicAPI]
public sealed record OrganizationEntryItem(
    Guid Id,
    Guid VaultId,
    EntryState State,
    Instant UpdatedAt,
    MemberIndexEnvelopeContract MemberIndex);

[PublicAPI]
public sealed record SearchOrganizationEntriesResponse(IReadOnlyList<OrganizationEntryItem> Items);

[UsedImplicitly]
internal sealed class SearchOrganizationEntriesValidator : Validator<SearchOrganizationEntriesRequest>
{
    public SearchOrganizationEntriesValidator() =>
        RuleFor(x => x.Limit!.Value).InclusiveBetween(1, 100).When(x => x.Limit is not null);
}

[PublicAPI]
internal sealed class SearchOrganizationEntriesEndpoint(VaultDomainReadContext domainReadContext)
    : Endpoint<SearchOrganizationEntriesRequest, SearchOrganizationEntriesResponse>
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 100;

    public override void Configure()
    {
        Get("api/entries");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.GrantManage);
        this.RequireEmailVerified();
        Summary(summary =>
        {
            summary.Summary = "List encrypted Entry indexes across Member Vaults";
            summary.Description = "Returns only structural state and encrypted MemberIndex projections for Vaults the Member can access. Matching is performed locally after unlock.";
        });
        Tags("Vault/Entries");
    }

    public override async Task HandleAsync(SearchOrganizationEntriesRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId()!.Value;
        var userId = User.GetUserId()!.Value;
        var limit = Math.Min(req.Limit ?? DefaultLimit, MaxLimit);

        var entries = await (
                from entry in domainReadContext.Entries
                join vault in domainReadContext.Vaults
                    on new { entry.OrganizationId, Id = entry.VaultId }
                    equals new { vault.OrganizationId, vault.Id }
                where entry.OrganizationId == organizationId
                      && entry.State == EntryState.Active
                      && vault.VaultMembers.Any(member => member.UserId == userId)
                orderby entry.UpdatedAt descending, entry.Id descending
                select entry)
            .Take(limit)
            .ToListAsync(ct);

        await Send.OkAsync(new SearchOrganizationEntriesResponse(entries.Select(entry => new OrganizationEntryItem(
            entry.Id,
            entry.VaultId,
            entry.State,
            entry.UpdatedAt,
            VaultEnvelopeContractMapper.ToContract(entry.GetMemberIndex()))).ToList()), ct);
    }
}
