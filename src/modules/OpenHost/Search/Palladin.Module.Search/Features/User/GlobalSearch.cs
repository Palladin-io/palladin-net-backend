using Palladin.Core.Security;
using Palladin.Module.Search.Contracts.Commands;
using Palladin.Module.Search.Contracts.ValueObjects;
using Palladin.Module.Search.Infrastructure.Persistence;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;

namespace Palladin.Module.Search.Features;

internal static class SearchConstants
{
    internal const string Route = "api/search";
    internal const int DefaultLimit = 20;
    internal const int MaxLimit = 25;
    internal const int MinQueryLength = 2;
    internal const int MaxQueryLength = 128;
}

[PublicAPI]
public sealed record GlobalSearchRequest
{
    public string? Q { get; init; }
    public int? Limit { get; init; }
}

[PublicAPI]
public sealed record SearchResultItem(
    string Type,
    Guid Id,
    string Name);

[PublicAPI]
public sealed record GlobalSearchResponse(IReadOnlyList<SearchResultItem> Results);

[UsedImplicitly]
internal sealed class GlobalSearchValidator : Validator<GlobalSearchRequest>
{
    public GlobalSearchValidator()
    {
        RuleFor(x => x.Limit!.Value)
            .InclusiveBetween(1, SearchConstants.MaxLimit)
            .When(x => x.Limit is not null);
        RuleFor(x => x.Q).MaximumLength(SearchConstants.MaxQueryLength);
    }
}

// Reads only the command-fed Agent/Member administrative catalog. The body query is ephemeral: it is
// used in this parameterized SELECT and is never logged, tagged, published, cached or persisted.
[PublicAPI]
internal sealed class GlobalSearchEndpoint(SearchDomainReadContext domainReadContext)
    : Endpoint<GlobalSearchRequest, GlobalSearchResponse>
{
    public override void Configure()
    {
        Post(SearchConstants.Route);
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Options(builder => builder.AllowNonActiveOrganizationMembership());
        // Members are visible within their organization; Agent results additionally require AgentManage.
        Summary(summary =>
        {
            summary.Summary = "Search the administrative Agent and Member catalog";
            summary.Description = "Accepts the ephemeral query in the request body and returns bounded, organization-scoped Agent and Member matches. Vault and Entry matching is exclusively client-side after unlock. Agent results require AgentManage.";
        });
        Tags("Search/Search");
    }

    public override async Task HandleAsync(GlobalSearchRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId();
        if (organizationId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var query = req.Q?.Trim();
        if (string.IsNullOrEmpty(query) || query.Length < SearchConstants.MinQueryLength)
        {
            await Send.OkAsync(new GlobalSearchResponse([]), ct);
            return;
        }

        var permissions = User.GetPermissions();
        var limit = Math.Clamp(req.Limit ?? SearchConstants.DefaultLimit, 1, SearchConstants.MaxLimit);
        var pattern = $"%{EscapeLikePattern(query)}%";

        var rows = await domainReadContext.Items
            .Where(i => i.OrganizationId == organizationId.Value
                        && !i.IsRemoved
                        && EF.Functions.ILike(i.SearchText, pattern, "\\")
                        && (i.Type != SearchItemTypes.Agent || (permissions & Permission.AgentManage) != 0))
            .OrderBy(i => i.Type)
            .ThenBy(i => i.Name)
            .ThenBy(i => i.Id)
            .Take(limit)
            .Select(i => new SearchResultItem(i.Type, i.Id, i.Name))
            .ToListAsync(ct);

        await Send.OkAsync(new GlobalSearchResponse(rows), ct);
    }

    private static string EscapeLikePattern(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}
