using Palladin.Core.Security;
using Palladin.Module.Agents.Domain;
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
public sealed record RevokeApiKeyRequest
{
    public Guid KeyId { get; init; }
}

[PublicAPI]
internal sealed class RevokeApiKeyEndpoint(
    AgentsDomainReadContext domainReadContext,
    AgentsDomainWriteContext domainWriteContext,
    IMemoryCache cache,
    IClock clock) : Endpoint<RevokeApiKeyRequest>
{
    public override void Configure()
    {
        Delete("api/api-keys/{KeyId}");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.WriteApiKey);
        this.RequireEmailVerified();
        Summary(summary =>
        {
            summary.Summary = "Revoke an API key";
            summary.Description = "Permanently revokes an API key. Any agents using this key will immediately lose access.";
        });
        Tags("Agents/ApiKeys");
    }

    public override async Task HandleAsync(RevokeApiKeyRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId();
        var userId = User.GetUserId();
        if (organizationId is null || userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var apiKey = await domainWriteContext.ApiKeys
            .FirstOrDefaultAsync(x => x.Id == req.KeyId && x.OrganizationId == organizationId, ct);

        if (apiKey is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (apiKey.Status == ApiKeyStatus.Revoked)
        {
            await Send.NoContentAsync(ct);
            return;
        }

        var actorName = await ApiKeyActor.ResolveActorNameAsync(domainReadContext, userId.Value, ct);
        apiKey.Revoke(userId.Value, actorName, clock.GetCurrentInstant());
        await domainWriteContext.CommitAsync(ct);

        cache.Remove(AgentAuthenticationHandler.ApiKeyCacheKey(apiKey.KeyHash));

        await Send.NoContentAsync(ct);
    }
}
