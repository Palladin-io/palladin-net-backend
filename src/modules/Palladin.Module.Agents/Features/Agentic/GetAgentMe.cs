using Palladin.Core.Types;
using Palladin.Module.Agents.Domain;
using Palladin.Module.Agents.Infrastructure.AgentAuth;
using Palladin.Module.Agents.Infrastructure.Persistence;
using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Palladin.Module.Agents.Features;

[PublicAPI]
public sealed record GetAgentMeResponse(
    Guid AgentId,
    Guid OrganizationId,
    string? Name,
    AgentStatus Status,
    Instant? EnrolledAt,
    Instant CreatedAt);

[PublicAPI]
internal sealed class GetAgentMeEndpoint(AgentsDomainReadContext domainReadContext)
    : EndpointWithoutRequest<GetAgentMeResponse>
{
    public override void Configure()
    {
        Get("api/agent/me");
        AuthSchemes(AgentAuthenticationOptions.SchemeName);
        Summary(summary =>
        {
            summary.Summary = "Get the authenticated agent's profile";
            summary.Description = "Returns the profile of the agent identified by the X-Agent-Key header. Requires an active agent and a valid organization API key.";
        });
        Tags("Agents/Agents");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var agentId = User.GetAgentId();
        if (agentId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var agent = await domainReadContext.Agents
            .Where(x => x.Id == agentId)
            .Select(x => new GetAgentMeResponse(
                x.Id,
                x.OrganizationId,
                x.Name,
                x.Status,
                x.EnrolledAt,
                x.CreatedAt))
            .FirstOrDefaultAsync(ct);

        if (agent is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(agent, ct);
    }
}
