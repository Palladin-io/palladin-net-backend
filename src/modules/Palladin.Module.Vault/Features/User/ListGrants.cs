using Palladin.Core.Api;
using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Vault.Infrastructure.Authorization;
using Palladin.Module.Vault.Infrastructure.Persistence;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record ListGrantsRequest : IRequiresVaultMembership
{
    public Guid VaultId { get; init; }
    public GrantStatus? Status { get; init; }
    public Guid? AgentId { get; init; }
    public string? Cursor { get; init; }
    public int? PageSize { get; init; }
}

[PublicAPI]
public sealed record ListGrantsResponse(IReadOnlyList<GrantResponse> Items, string? NextCursor);

[UsedImplicitly]
internal sealed class ListGrantsValidator : Validator<ListGrantsRequest>
{
    public ListGrantsValidator()
    {
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.PageSize!.Value).InclusiveBetween(1, 100).When(x => x.PageSize is not null);
    }
}

[PublicAPI]
internal sealed class ListGrantsEndpoint(VaultDomainReadContext domainReadContext) : Endpoint<ListGrantsRequest, ListGrantsResponse>
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;

    public override void Configure()
    {
        Get("api/vaults/{vaultId:guid}/grants");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.GrantManage);
        this.RequireEmailVerified();
        this.RequireVaultMembership();
        Summary(summary =>
        {
            summary.Summary = "List grants in a vault";
            summary.Description = "Returns grant metadata and encrypted Agent reason envelopes ordered oldest-first by (CreatedAt, Id). Filter by status and agent. Credential grant payload ciphertext, plaintext secrets and private or unwrapped keys are never included. Use NextCursor for the next page.";
        });
        Tags("Vault/Grants");
    }

    public override async Task HandleAsync(ListGrantsRequest req, CancellationToken ct)
    {
        var pageSize = Math.Min(req.PageSize ?? DefaultPageSize, MaxPageSize);
        var cursor = InstantCursor.Decode(req.Cursor);

        var query = domainReadContext.Grants.Where(g => g.VaultId == req.VaultId);

        if (req.Status is not null)
        {
            query = query.Where(g => g.Status == req.Status);
        }

        if (req.AgentId is not null)
        {
            query = query.Where(g => g.AgentId == req.AgentId);
        }

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
        await Send.OkAsync(new ListGrantsResponse(rows, nextCursor), ct);
    }
}
