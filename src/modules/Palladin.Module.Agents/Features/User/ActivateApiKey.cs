using Palladin.Core.Security;
using Palladin.Module.Agents.Domain;
using Palladin.Module.Agents.Infrastructure.Persistence;
using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Palladin.Module.Agents.Features;

[PublicAPI]
internal sealed class ActivateApiKeyEndpoint(
    AgentsDomainReadContext domainReadContext,
    AgentsDomainWriteContext domainWriteContext,
    IClock clock) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("api/api-keys/{KeyId}/activate");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.WriteApiKey);
        this.RequireEmailVerified();
        Summary(summary =>
        {
            summary.Summary = "Activate a revoked API key";
            summary.Description = "Re-activates a previously revoked API key. Agents using this key will regain access immediately.";
        });
        Tags("Agents/ApiKeys");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var keyId = Route<Guid>("KeyId");
        var organizationId = User.GetOrganizationId();
        var userId = User.GetUserId();
        if (organizationId is null || userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var apiKey = await domainWriteContext.ApiKeys
            .FirstOrDefaultAsync(x => x.Id == keyId && x.OrganizationId == organizationId, ct);

        if (apiKey is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (apiKey.Status == ApiKeyStatus.Active)
        {
            await Send.NoContentAsync(ct);
            return;
        }

        var actorName = await ApiKeyActor.ResolveActorNameAsync(domainReadContext, userId.Value, ct);
        apiKey.Activate(userId.Value, actorName, clock.GetCurrentInstant());
        await domainWriteContext.CommitAsync(ct);

        await Send.NoContentAsync(ct);
    }
}
