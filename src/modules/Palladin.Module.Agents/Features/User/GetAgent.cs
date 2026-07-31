using Palladin.Core.Security;
using Palladin.Module.Agents.Infrastructure.Persistence;
using Palladin.Module.Agents.Shared;
using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;

namespace Palladin.Module.Agents.Features;

[PublicAPI]
public sealed record GetAgentRequest
{
    public Guid AgentId { get; init; }
}

[PublicAPI]
internal sealed class GetAgentEndpoint(AgentsDomainReadContext domainReadContext)
    : Endpoint<GetAgentRequest, AgentSummary>
{
    public override void Configure()
    {
        Get("api/agents/{AgentId}");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.AgentManage);
        this.RequireEmailVerified();
        Summary(summary =>
        {
            summary.Summary = "Get an organization agent";
            summary.Description = "Returns a single agent belonging to the authenticated user's organization.";
        });
        Tags("Agents/Agents");
    }

    public override async Task HandleAsync(GetAgentRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId();
        if (organizationId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var agent = await domainReadContext.Agents
            .Where(x => x.Id == req.AgentId && x.OrganizationId == organizationId)
            .ToSummaryProjection()
            .FirstOrDefaultAsync(ct);

        if (agent is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var userNames = await AgentSummaryMapper.LoadUserNamesAsync(domainReadContext, [agent], ct);

        await Send.OkAsync(agent.ToSummary(userNames, includeFullPublicKey: true), ct);
    }
}
