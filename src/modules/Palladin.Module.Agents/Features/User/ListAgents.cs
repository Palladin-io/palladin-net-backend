using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Agents.Domain;
using Palladin.Module.Agents.Infrastructure.Persistence;
using Palladin.Module.Agents.Shared;
using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Palladin.Module.Agents.Features;

[PublicAPI]
public sealed record AgentSummary(
    Guid AgentId,
    string? Name,
    string? Description,
    string? Type,
    string? IconKey,
    string? IconColor,
    AgentStatus Status,
    string PublicKeyPrefix,
    string PublicKeySuffix,
    string? PublicKey,
    uint RecipientKeyVersion,
    Instant CreatedAt,
    Instant? EnrolledAt,
    string? EnrolledByName,
    Instant? DeactivatedAt,
    string? DeactivatedByName,
    Instant? ReactivatedAt,
    string? ReactivatedByName,
    Instant? LastAccessAt,
    string? LastIp,
    string? LastHostname);

[PublicAPI]
public sealed record ListAgentsResponse(IReadOnlyList<AgentSummary> Items);

[PublicAPI]
internal sealed class ListAgentsEndpoint(AgentsDomainReadContext domainReadContext)
    : EndpointWithoutRequest<ListAgentsResponse>
{
    public override void Configure()
    {
        Get("api/agents");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.AgentManage);
        this.RequireEmailVerified();
        Summary(summary =>
        {
            summary.Summary = "List organization agents";
            summary.Description = "Returns all agents belonging to the authenticated user's organization, regardless of status.";
        });
        Tags("Agents/Agents");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId();
        if (organizationId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var agents = await domainReadContext.Agents
            .Where(x => x.OrganizationId == organizationId)
            .OrderByDescending(x => x.CreatedAt)
            .ToSummaryProjection()
            .ToListAsync(ct);

        var userNames = await AgentSummaryMapper.LoadUserNamesAsync(domainReadContext, agents, ct);
        var items = agents.Select(a => a.ToSummary(userNames)).ToList();

        await Send.OkAsync(new ListAgentsResponse(items), ct);
    }
}
