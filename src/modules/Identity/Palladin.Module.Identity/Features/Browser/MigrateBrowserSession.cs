using FastEndpoints;
using JetBrains.Annotations;
using Palladin.Module.Identity.Infrastructure.BrowserSessions;
using Palladin.Module.Identity.Shared;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
internal sealed class MigrateBrowserSessionEndpoint(RefreshAccessTokenOperation operation, BrowserSessionResponseWriter writer)
    : IdentityOperationEndpoint<RefreshAccessTokenRequest, object>
{
    public override void Configure()
    {
        Post("api/browser/auth/migrate");
        // One-time legacy credential exchange; protected by the browser origin boundary.
        AllowAnonymous();
        Validator<RefreshAccessTokenValidator>();
        Tags("Identity/Browser");
    }

    public override async Task HandleAsync(RefreshAccessTokenRequest req, CancellationToken ct)
    {
        if (BrowserSessionCookie.Read(HttpContext) is not null)
        {
            await Send.StatusCodeAsync(409, ct);
            return;
        }
        var result = await operation.ExecuteAsync(req, ct, forceRotation: true);
        await SendResultAsync(await writer.MapAsync(result, HttpContext, ct), ct);
    }
}
