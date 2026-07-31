using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Api;
using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record SearchOrganizationEntriesRequest
{
    public string? Cursor { get; init; }
    public int? PageSize { get; init; }
}

[PublicAPI]
public sealed record OrganizationEntryItem(
    Guid Id,
    Guid VaultId,
    EntryState State,
    Instant UpdatedAt,
    MemberIndexEnvelopeContract MemberIndex);

[PublicAPI]
public sealed record SearchOrganizationEntriesResponse(IReadOnlyList<OrganizationEntryItem> Items, string? NextCursor);

[UsedImplicitly]
internal sealed class SearchOrganizationEntriesValidator : Validator<SearchOrganizationEntriesRequest>
{
    public SearchOrganizationEntriesValidator() =>
        RuleFor(x => x.PageSize!.Value).InclusiveBetween(1, 100).When(x => x.PageSize is not null);
}

[PublicAPI]
internal sealed class SearchOrganizationEntriesEndpoint(VaultDomainReadContext domainReadContext)
    : Endpoint<SearchOrganizationEntriesRequest, SearchOrganizationEntriesResponse>
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 100;

    public override void Configure()
    {
        Get("api/entries");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.GrantManage);
        this.RequireEmailVerified();
        Summary(summary =>
        {
            summary.Summary = "List encrypted Entry indexes across Member Vaults";
            summary.Description = "Returns a cursor-paginated stream of structural state and encrypted MemberIndex projections for Vaults the Member can access. Matching is performed locally after unlock.";
        });
        Tags("Vault/Entries");
    }

    public override async Task HandleAsync(SearchOrganizationEntriesRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId()!.Value;
        var userId = User.GetUserId()!.Value;
        var pageSize = Math.Min(req.PageSize ?? DefaultPageSize, MaxPageSize);
        var cursor = InstantCursor.Decode(req.Cursor);

        var query =
                from entry in domainReadContext.Entries
                join vault in domainReadContext.Vaults
                    on new { entry.OrganizationId, Id = entry.VaultId }
                    equals new { vault.OrganizationId, vault.Id }
                where entry.OrganizationId == organizationId
                      && entry.State == EntryState.Active
                      && vault.VaultMembers.Any(member => member.UserId == userId)
                select entry;
        if (cursor is not null)
        {
            query = query.Where(entry =>
                entry.UpdatedAt < cursor.Timestamp
                || (entry.UpdatedAt == cursor.Timestamp && entry.Id.CompareTo(cursor.Id) < 0));
        }

        var entries = await query
            .OrderByDescending(entry => entry.UpdatedAt)
            .ThenByDescending(entry => entry.Id)
            .Take(pageSize + 1)
            .ToListAsync(ct);
        string? nextCursor = null;
        if (entries.Count > pageSize)
        {
            var last = entries[pageSize - 1];
            nextCursor = InstantCursor.Encode(last.UpdatedAt, last.Id);
            entries = entries.Take(pageSize).ToList();
        }

        await Send.OkAsync(new SearchOrganizationEntriesResponse(entries.Select(entry => new OrganizationEntryItem(
            entry.Id,
            entry.VaultId,
            entry.State,
            entry.UpdatedAt,
            VaultEnvelopeContractMapper.ToContract(entry.GetMemberIndex()))).ToList(), nextCursor), ct);
    }
}
