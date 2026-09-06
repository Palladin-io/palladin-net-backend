using Palladin.Core.Security;
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
public sealed record DeleteApiKeyRequest
{
    public Guid KeyId { get; init; }
}

[PublicAPI]
internal sealed class DeleteApiKeyEndpoint(
    AgentsDomainReadContext domainReadContext,
    AgentsDomainWriteContext domainWriteContext,
    IMemoryCache cache,
    IClock clock) : Endpoint<DeleteApiKeyRequest>
{
    public override void Configure()
    {
        Delete("api/api-keys/{KeyId}/permanent");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.WriteApiKey);
        this.RequireEmailVerified();
        Summary(summary =>
        {
            summary.Summary = "Permanently delete an API key";
            summary.Description = "Hard-deletes an API key record from the system. This is irreversible and removes all history. For a softer option that preserves history, use revoke instead.";
        });
        Tags("Agents/ApiKeys");
    }

    public override async Task HandleAsync(DeleteApiKeyRequest req, CancellationToken ct)
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

        var actorName = await ApiKeyActor.ResolveActorNameAsync(domainReadContext, userId.Value, ct);
        apiKey.MarkDeleted(userId.Value, actorName, clock.GetCurrentInstant());
        domainWriteContext.Remove(apiKey);
        try
        {
            await domainWriteContext.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            domainWriteContext.Clear();
            await Send.StatusCodeAsync(409, ct);
            return;
        }

        AgentAuthenticationHandler.InvalidateApiKeyCache(cache, apiKey.KeyHash);

        await Send.NoContentAsync(ct);
    }
}
