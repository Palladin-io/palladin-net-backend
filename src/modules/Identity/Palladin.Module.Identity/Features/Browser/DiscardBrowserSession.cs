using Palladin.Module.Identity.Infrastructure.Jwt;
using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Security;
using Palladin.Module.Identity.Infrastructure;
using Palladin.Module.Identity.Infrastructure.Persistence;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
internal sealed class DiscardBrowserSessionEndpoint(IdentityDomainWriteContext context,
    RefreshTokenLineageRevoker revoker, IClock clock) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("api/browser/auth/discard");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Options(builder => builder.AllowNonActiveOrganizationMembership());
        Tags("Identity/Browser");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var sessionId = User.GetBrowserSessionId();
        if (sessionId is null)
        {
            await Send.ForbiddenAsync(ct);
            return;
        }
        var token = await context.RefreshTokens.FirstOrDefaultAsync(token => token.UserId == User.GetUserId()
            && token.OrganizationId == User.GetOrganizationId()
            && (token.SessionId ?? token.Id) == sessionId, ct);
        if (token is not null && !await revoker.RevokeForLogoutAsync(token.UserId, token.Id, clock.GetCurrentInstant(), ct))
        {
            await Send.StatusCodeAsync(409, ct);
            return;
        }
        // Cleanup responses can arrive after a newer login in another document.
        // Revoke the signed lineage without emitting a delayed cookie deletion.
        await Send.NoContentAsync(ct);
    }
}
