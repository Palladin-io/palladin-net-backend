using Palladin.Core.Api;
using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Vault.Infrastructure.Persistence;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record ListPendingGrantsRequest
{
    public string? Cursor { get; init; }
    public int? PageSize { get; init; }
}

[PublicAPI]
public sealed record ListPendingGrantsResponse(IReadOnlyList<GrantResponse> Items, string? NextCursor);

[UsedImplicitly]
internal sealed class ListPendingGrantsValidator : Validator<ListPendingGrantsRequest>
{
    public ListPendingGrantsValidator()
    {
        RuleFor(x => x.PageSize!.Value).InclusiveBetween(1, 100).When(x => x.PageSize is not null);
    }
}

[PublicAPI]
internal sealed class ListPendingGrantsEndpoint(VaultDomainReadContext domainReadContext)
    : Endpoint<ListPendingGrantsRequest, ListPendingGrantsResponse>
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;

    public override void Configure()
    {
        Get("api/dashboard/pending-grants");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.GrantManage);
        this.RequireEmailVerified();
        Summary(summary =>
        {
            summary.Summary = "List pending grants across the organization";
            summary.Description = "Cross-vault list of grants awaiting approval, scoped to the caller's organization, ordered oldest-first by (CreatedAt, Id). Includes only the Agent-signed encrypted reason needed for local Member decryption and verification; credential ciphertext and private key material are never included.";
        });
        Tags("Vault/Grants");
    }

    public override async Task HandleAsync(ListPendingGrantsRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId()!.Value;
        var pageSize = Math.Min(req.PageSize ?? DefaultPageSize, MaxPageSize);
        var cursor = InstantCursor.Decode(req.Cursor);

        var query = domainReadContext.Grants
            .Where(g => g.OrganizationId == organizationId && g.Status == GrantStatus.Pending);

        if (cursor is not null)
        {
            query = query.Where(g =>
                g.CreatedAt > cursor.Timestamp
                || (g.CreatedAt == cursor.Timestamp && g.Id.CompareTo(cursor.Id) > 0));
        }

        var rows = await query
            .OrderBy(g => g.CreatedAt)
            .ThenBy(g => g.Id)
            .Take(pageSize + 1)
            .Select(GrantProjection.ToResponse(domainReadContext))
            .ToListAsync(ct);

        string? nextCursor = null;
        if (rows.Count > pageSize)
        {
            var last = rows[pageSize - 1];
            nextCursor = InstantCursor.Encode(last.CreatedAt, last.Id);
            rows = rows.Take(pageSize).ToList();
        }

        await GrantProjection.ApplyEncryptedReasonsAsync(domainReadContext, rows, ct);
        GrantProjection.ApplyAgentSigningIdentity(rows);
        await GrantProjection.ApplyCanGrantAgainAsync(domainReadContext, rows, ct);
        await Send.OkAsync(new ListPendingGrantsResponse(rows, nextCursor), ct);
    }
}
