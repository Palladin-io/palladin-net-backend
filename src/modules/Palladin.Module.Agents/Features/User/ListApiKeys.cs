using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Agents.Domain;
using Palladin.Module.Agents.Infrastructure.Persistence;
using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Palladin.Module.Agents.Features;

[PublicAPI]
public sealed record ApiKeySummary(
    Guid ApiKeyId,
    string Name,
    string KeySuffix,
    ApiKeyStatus Status,
    Instant CreatedAt,
    string CreatedByName,
    Instant? RevokedAt,
    string? RevokedByName,
    int ActiveAgentCount);

[PublicAPI]
public sealed record ListApiKeysResponse(IReadOnlyList<ApiKeySummary> Items);

[PublicAPI]
internal sealed class ListApiKeysEndpoint(AgentsDomainReadContext domainReadContext)
    : EndpointWithoutRequest<ListApiKeysResponse>
{
    public override void Configure()
    {
        Get("api/api-keys");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.ReadApiKey);
        this.RequireEmailVerified();
        Summary(summary =>
        {
            summary.Summary = "List organization API keys";
            summary.Description = "Returns API keys belonging to the authenticated user's organization. The plaintext key value is never returned.";
        });
        Tags("Agents/ApiKeys");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId();
        if (organizationId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var keys = await domainReadContext.ApiKeys
            .Where(x => x.OrganizationId == organizationId)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new
            {
                x.Id,
                x.Name,
                x.KeySuffix,
                x.Status,
                x.CreatedAt,
                x.CreatedBy,
                x.RevokedAt,
                x.RevokedBy,
            })
            .ToListAsync(ct);

        var userIds = keys
            .Select(k => k.CreatedBy)
            .Concat(keys.Where(k => k.RevokedBy.HasValue).Select(k => k.RevokedBy!.Value))
            .Distinct()
            .ToList();

        var users = await domainReadContext.Users
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName })
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);

        var keyIds = keys.Select(k => k.Id).ToList();
        var agentCounts = await domainReadContext.Agents
            .Where(a => a.OrganizationId == organizationId
                && a.Status == AgentStatus.Active
                && a.LastUsedApiKeyId != null
                && keyIds.Contains(a.LastUsedApiKeyId.Value))
            .GroupBy(a => a.LastUsedApiKeyId!.Value)
            .Select(g => new { KeyId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.KeyId, x => x.Count, ct);

        var items = keys
            .Select(k => new ApiKeySummary(
                k.Id,
                k.Name,
                k.KeySuffix,
                k.Status,
                k.CreatedAt,
                users.GetValueOrDefault(k.CreatedBy, string.Empty),
                k.RevokedAt,
                k.RevokedBy.HasValue ? users.GetValueOrDefault(k.RevokedBy.Value) : null,
                agentCounts.GetValueOrDefault(k.Id, 0)))
            .ToList();

        await Send.OkAsync(new ListApiKeysResponse(items), ct);
    }
}
