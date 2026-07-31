using System.Globalization;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Api;
using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Vault.Infrastructure.Authorization;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record ListEntriesRequest : IRequiresVaultMembership
{
    public Guid VaultId { get; init; }
    public string? Cursor { get; init; }
    public int? PageSize { get; init; }
}

[PublicAPI]
public sealed record EntryListItem(
    Guid Id,
    EntryState State,
    string CurrentRevision,
    string MemberIndexRevision,
    uint CurrentKeyVersion,
    Instant CreatedAt,
    Instant UpdatedAt,
    MemberIndexEnvelopeContract MemberIndex);

[PublicAPI]
public sealed record ListEntriesResponse(IReadOnlyList<EntryListItem> Items, string? NextCursor);

[UsedImplicitly]
internal sealed class ListEntriesValidator : Validator<ListEntriesRequest>
{
    public ListEntriesValidator()
    {
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.PageSize!.Value).InclusiveBetween(1, 100).When(x => x.PageSize is not null);
    }
}

[PublicAPI]
internal sealed class ListEntriesEndpoint(VaultDomainReadContext domainReadContext) : Endpoint<ListEntriesRequest, ListEntriesResponse>
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;

    public override void Configure()
    {
        Get("api/vaults/{vaultId:guid}/entries");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.VaultManage);
        this.RequireEmailVerified();
        this.RequireVaultMembership();
        Summary(summary =>
        {
            summary.Summary = "List canonical encrypted Entries";
            summary.Description = "Returns structural Entry heads and encrypted MemberIndex projections. Names, usernames, domains, tags and other presentation fields are resolved locally after unlock.";
        });
        Tags("Vault/Entries");
    }

    public override async Task HandleAsync(ListEntriesRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId()!.Value;
        var pageSize = Math.Min(req.PageSize ?? DefaultPageSize, MaxPageSize);
        var cursor = InstantCursor.Decode(req.Cursor);
        var query = domainReadContext.Entries.Where(x =>
            x.OrganizationId == organizationId
            && x.VaultId == req.VaultId
            && x.State == EntryState.Active);

        if (cursor is not null)
        {
            query = query.Where(x =>
                x.CreatedAt > cursor.Timestamp
                || (x.CreatedAt == cursor.Timestamp && x.Id.CompareTo(cursor.Id) > 0));
        }

        var rows = await query
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .Take(pageSize + 1)
            .ToListAsync(ct);

        string? nextCursor = null;
        if (rows.Count > pageSize)
        {
            var last = rows[pageSize - 1];
            nextCursor = InstantCursor.Encode(last.CreatedAt, last.Id);
            rows = rows.Take(pageSize).ToList();
        }

        await Send.OkAsync(new ListEntriesResponse(rows.Select(x => new EntryListItem(
            x.Id,
            x.State,
            x.CurrentRevision.Value.ToString(CultureInfo.InvariantCulture),
            x.MemberIndexRevision.Value.ToString(CultureInfo.InvariantCulture),
            x.CurrentKeyVersion.Value,
            x.CreatedAt,
            x.UpdatedAt,
            VaultEnvelopeContractMapper.ToContract(x.GetMemberIndex()))).ToList(), nextCursor), ct);
    }
}
