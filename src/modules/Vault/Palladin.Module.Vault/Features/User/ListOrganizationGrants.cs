using Palladin.Core.Api;
using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Persistence;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record ListOrganizationGrantsRequest
{
    public GrantStatus? Status { get; init; }
    public Guid? AgentId { get; init; }
    public Guid? VaultId { get; init; }
    public Guid? EntryId { get; init; }
    public string? Query { get; init; }
    public string? Cursor { get; init; }
    public int? PageSize { get; init; }
}

[PublicAPI]
public sealed record ListOrganizationGrantsResponse(IReadOnlyList<GrantResponse> Items, string? NextCursor);

[UsedImplicitly]
internal sealed class ListOrganizationGrantsValidator : Validator<ListOrganizationGrantsRequest>
{
    public ListOrganizationGrantsValidator()
    {
        RuleFor(x => x.PageSize!.Value).InclusiveBetween(1, 100).When(x => x.PageSize is not null);
        RuleFor(x => x.Query!).MinimumLength(2).When(x => !string.IsNullOrEmpty(x.Query));
    }
}

[PublicAPI]
internal sealed class ListOrganizationGrantsEndpoint(VaultDomainReadContext domainReadContext)
    : Endpoint<ListOrganizationGrantsRequest, ListOrganizationGrantsResponse>
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;

    public override void Configure()
    {
        Get("api/grants");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.GrantManage);
        this.RequireEmailVerified();
        Summary(summary =>
        {
            summary.Summary = "List all grants across the organization (access / approval history)";
            summary.Description = "Org-scoped, all-statuses grant list with encrypted Agent reason envelopes for the Approvals history panel, newest first by (CreatedAt, Id). Filter by status, agent, Vault id, Entry id and a free-text query over authorized Agent/Entry projections. Credential grant payload ciphertext and plaintext secrets are never returned, and encrypted Vault display metadata is never resolved server-side.";
        });
        Tags("Vault/Grants");
    }

    public override async Task HandleAsync(ListOrganizationGrantsRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId()!.Value;
        var pageSize = Math.Min(req.PageSize ?? DefaultPageSize, MaxPageSize);
        var cursor = InstantCursor.Decode(req.Cursor);

        var query = domainReadContext.Grants.Where(g => g.OrganizationId == organizationId);

        if (req.Status is not null)
        {
            query = query.Where(g => g.Status == req.Status);
        }

        if (req.AgentId is not null)
        {
            query = query.Where(g => g.AgentId == req.AgentId);
        }

        if (req.VaultId is not null)
        {
            // Vault filter covers both FULL grants on the vault and GRANULAR grants on its entries —
            // both carry VaultId on the Grant row.
            query = query.Where(g => g.VaultId == req.VaultId);
        }

        if (req.EntryId is not null)
        {
            // Entry filter is GRANULAR-only by design ("just this entry"): a FULL grant covers the whole
            // vault and is intentionally excluded.
            query = query.Where(g => g is GranularGrant && ((GranularGrant)g).EntryId == req.EntryId);
        }

        if (!string.IsNullOrEmpty(req.Query))
        {
            // Temporary matching over the still-plaintext Agent and Entry projections only.
            // Encrypted Vault metadata is never matched on the server.
            var pattern = $"%{req.Query.Trim()}%";
            query = query.Where(g =>
                domainReadContext.Agents.Any(a => a.Id == g.AgentId && a.Name != null && EF.Functions.ILike(a.Name, pattern)));
        }

        if (cursor is not null)
        {
            // Newest-first paging: walk strictly older than the cursor (CreatedAt desc, Id desc tiebreak).
            query = query.Where(g =>
                g.CreatedAt < cursor.Timestamp
                || (g.CreatedAt == cursor.Timestamp && g.Id.CompareTo(cursor.Id) < 0));
        }

        var rows = await query
            .OrderByDescending(g => g.CreatedAt)
            .ThenByDescending(g => g.Id)
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
        await Send.OkAsync(new ListOrganizationGrantsResponse(rows, nextCursor), ct);
    }
}
