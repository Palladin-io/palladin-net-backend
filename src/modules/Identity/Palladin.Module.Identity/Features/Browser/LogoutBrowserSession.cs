using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Api;
using Palladin.Module.Identity.Infrastructure.BrowserSessions;
using Palladin.Module.Identity.Infrastructure.Jwt;
using Palladin.Module.Identity.Infrastructure.Persistence;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record LogoutBrowserSessionRequest(Guid? ExpectedSessionId);

[PublicAPI]
internal sealed class LogoutBrowserSessionEndpoint(IdentityDomainWriteContext context,
    RefreshTokenLineageRevoker revoker, IClock clock) : Endpoint<LogoutBrowserSessionRequest>
{
    public override void Configure()
    {
        Post("api/browser/auth/logout");
        // Cookie possession authorizes its own lineage revocation even after JWT expiry.
        AllowAnonymous();
        Tags("Identity/Browser");
    }

    public override async Task HandleAsync(LogoutBrowserSessionRequest req, CancellationToken ct)
    {
        var raw = BrowserSessionCookie.Read(HttpContext);
        if (raw is not null)
        {
            var hash = TokenService.HashToken(raw);
            var token = await context.RefreshTokens.SingleOrDefaultAsync(token => token.TokenHash == hash, ct);
            if (token is not null)
            {
                if (req.ExpectedSessionId is { } expected && expected != (token.SessionId ?? token.Id))
                {
                    await Send.StatusCodeAsync(409, ct);
                    return;
                }
                if (!await revoker.RevokeForLogoutAsync(token.UserId, token.Id, clock.GetCurrentInstant(), ct))
                {
                    AddError(ErrorResponses.General("session-logout-conflict"));
                    await Send.ErrorsAsync(409, ct);
                    return;
                }
            }
            BrowserSessionCookie.Delete(HttpContext);
        }
        await Send.NoContentAsync(ct);
    }
}
