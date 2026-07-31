using Palladin.Core.Api;
using Palladin.Core.Security;
using Palladin.Module.Agents.Infrastructure.Persistence;
using Palladin.Module.Agents.Shared;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;

namespace Palladin.Module.Agents.Features;

[PublicAPI]
public sealed record ListApiKeyAgentsRequest
{
    public Guid ApiKeyId { get; init; }
    public string? Cursor { get; init; }
    public int? PageSize { get; init; }
}

[PublicAPI]
public sealed record ListApiKeyAgentsResponse(IReadOnlyList<AgentSummary> Items, string? NextCursor);

[UsedImplicitly]
internal sealed class ListApiKeyAgentsValidator : Validator<ListApiKeyAgentsRequest>
{
    public ListApiKeyAgentsValidator()
    {
        RuleFor(x => x.PageSize!.Value).InclusiveBetween(1, 100).When(x => x.PageSize is not null);
    }
}

[PublicAPI]
internal sealed class ListApiKeyAgentsEndpoint(AgentsDomainReadContext domainReadContext)
    : Endpoint<ListApiKeyAgentsRequest, ListApiKeyAgentsResponse>
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;

    public override void Configure()
    {
        Get("api/api-keys/{ApiKeyId}/agents");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.ReadApiKey);
        this.RequireEmailVerified();
        Summary(summary =>
        {
            summary.Summary = "List agents belonging to an API key";
            summary.Description = "Returns the organization's agents whose last used API key is the given key, newest-first, with cursor pagination. No secrets or ciphertext are included.";
        });
        Tags("Agents/ApiKeys");
    }

    public override async Task HandleAsync(ListApiKeyAgentsRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId();
        if (organizationId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var pageSize = Math.Min(req.PageSize ?? DefaultPageSize, MaxPageSize);
        var cursor = InstantCursor.Decode(req.Cursor);

        var query = domainReadContext.Agents
            .Where(x => x.OrganizationId == organizationId && x.LastUsedApiKeyId == req.ApiKeyId);

        if (cursor is not null)
        {
            query = query.Where(x =>
                x.CreatedAt < cursor.Timestamp
                || (x.CreatedAt == cursor.Timestamp && x.Id.CompareTo(cursor.Id) < 0));
        }

        var projections = await query
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Take(pageSize + 1)
            .ToSummaryProjection()
            .ToListAsync(ct);

        string? nextCursor = null;
        if (projections.Count > pageSize)
        {
            var last = projections[pageSize - 1];
            nextCursor = InstantCursor.Encode(last.CreatedAt, last.Id);
            projections = projections.Take(pageSize).ToList();
        }

        var userNames = await AgentSummaryMapper.LoadUserNamesAsync(domainReadContext, projections, ct);
        var items = projections.Select(p => p.ToSummary(userNames)).ToList();

        await Send.OkAsync(new ListApiKeyAgentsResponse(items, nextCursor), ct);
    }
}
