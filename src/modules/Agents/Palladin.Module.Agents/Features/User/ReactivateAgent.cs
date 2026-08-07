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
internal sealed class ReactivateAgentEndpoint(
    AgentsDomainWriteContext domainWriteContext,
    IClock clock) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("api/agents/{AgentId}/reactivate");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.AgentManage);
        this.RequireEmailVerified();
        Summary(summary =>
        {
            summary.Summary = "Reactivate a deactivated agent";
            summary.Description = "Restores access for a previously deactivated agent.";
        });
        Tags("Agents/Agents");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var agentId = Route<Guid>("AgentId");
        var organizationId = User.GetOrganizationId();
        var userId = User.GetUserId();
        if (organizationId is null || userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var agent = await domainWriteContext.Agents
            .FirstOrDefaultAsync(x => x.Id == agentId && x.OrganizationId == organizationId, ct);

        if (agent is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (agent.Status != AgentStatus.Deactivated)
        {
            AddError("Only a deactivated agent can be reactivated.");
            await Send.ErrorsAsync(400, ct);
            return;
        }

        agent.Reactivate(userId.Value, User.GetDisplayName(), clock.GetCurrentInstant());
        await domainWriteContext.CommitAsync(ct);

        await Send.NoContentAsync(ct);
    }
}
