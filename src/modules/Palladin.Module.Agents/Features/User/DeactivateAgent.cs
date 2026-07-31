using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Core.Guid;
using Palladin.Module.Agents.Domain;
using Palladin.Module.Agents.Infrastructure.Persistence;
using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Palladin.Module.Agents.Features;

[PublicAPI]
internal sealed class DeactivateAgentEndpoint(
    AgentsDomainWriteContext domainWriteContext,
    IGuidProvider guidProvider,
    IClock clock) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("api/agents/{AgentId}/deactivate");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.AgentManage);
        this.RequireEmailVerified();
        Summary(summary =>
        {
            summary.Summary = "Deactivate an agent";
            summary.Description = "Starts staged deactivation. New access is denied immediately and current Vault access is removed monotonically as each Vault rotation commits.";
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

        if (agent.Status == AgentStatus.Deactivated)
        {
            AddError("Agent is already deactivated.");
            await Send.ErrorsAsync(400, ct);
            return;
        }

        agent.RequestDeactivation(guidProvider.Generate(), userId.Value, clock.GetCurrentInstant());
        await domainWriteContext.CommitAsync(ct);

        await Send.NoContentAsync(ct);
    }
}
