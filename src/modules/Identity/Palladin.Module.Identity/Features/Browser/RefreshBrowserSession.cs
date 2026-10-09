using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Palladin.Module.Identity.Infrastructure.BrowserSessions;
using Palladin.Module.Identity.Infrastructure.Jwt;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Shared;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
internal sealed class RefreshBrowserSessionEndpoint(RefreshAccessTokenOperation operation,
    IdentityDomainWriteContext context, ITokenService tokenService) : EndpointWithoutRequest<BrowserAuthSessionResponse>
{
    public override void Configure()
    {
        Post("api/browser/auth/refresh");
        // The HttpOnly cookie is the credential; the browser origin guard runs before endpoint binding.
        AllowAnonymous();
        Tags("Identity/Browser");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var token = BrowserSessionCookie.Read(HttpContext);
        if (string.IsNullOrEmpty(token))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }
        var result = await operation.ExecuteAsync(new RefreshAccessTokenRequest { RefreshToken = token }, ct);
        if (result.Error is { } error)
        {
            AddError(error);
            await Send.ErrorsAsync(result.StatusCode, ct);
            return;
        }
        if (result.Body is not { } body)
        {
            await Send.StatusCodeAsync(result.StatusCode, ct);
            return;
        }
        var hash = TokenService.HashToken(body.RefreshToken);
        var session = await context.RefreshTokens.SingleAsync(token => token.TokenHash == hash, ct);
        BrowserSessionCookie.Write(HttpContext, body.RefreshToken, session.ExpiresAt);
        await Send.OkAsync(new BrowserAuthSessionResponse(tokenService.BindBrowserSession(body.AccessToken, session.SessionId ?? session.Id), session.SessionId ?? session.Id,
            body.UserId, body.IsOnboarded, body.EmailVerified, body.WaitlistDeveloperBenefitStartedAt,
            body.WaitlistDeveloperBenefitEndsAt), ct);
    }
}
