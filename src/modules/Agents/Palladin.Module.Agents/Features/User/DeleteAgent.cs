using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Agents.Infrastructure.AgentAuth;
using Palladin.Module.Agents.Infrastructure.Persistence;
using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NodaTime;

namespace Palladin.Module.Agents.Features;

[PublicAPI]
internal sealed class DeleteAgentEndpoint(
    AgentsDomainReadContext domainReadContext,
    AgentsDomainWriteContext domainWriteContext,
    IMemoryCache cache,
    IClock clock) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Delete("api/agents/{AgentId}");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.AgentManage);
        this.RequireEmailVerified();
        Summary(summary =>
        {
            summary.Summary = "Delete an agent";
            summary.Description = "Permanently deletes a deactivated agent. Cascades removal of the agent's grants and read-model replica in the Vault module. An agent must be deactivated first; deleting an active or pending agent is rejected.";
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
            AddError("Agent must be deactivated before it can be deleted.");
            await Send.ErrorsAsync(409, ct);
            return;
        }

        var credentialHashes = await domainReadContext.ApiKeyCredentials
            .Where(x => x.AgentId == agent.Id)
            .Select(x => x.KeyHash)
            .ToListAsync(ct);

        agent.Delete(userId.Value, User.GetDisplayName(), clock.GetCurrentInstant());
        domainWriteContext.Remove(agent);
        await domainWriteContext.CommitAsync(ct);

        foreach (var credentialHash in credentialHashes)
        {
            AgentAuthenticationHandler.InvalidateApiKeyCache(cache, credentialHash);
        }

        await Send.NoContentAsync(ct);
    }
}
