using Microsoft.AspNetCore.Http;
using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Palladin.Module.Identity.Infrastructure.BrowserSessions;
using Palladin.Module.Identity.Infrastructure.Jwt;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Shared;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record RefreshBrowserSessionRequest
{
    public Guid? ExpectedSessionId { get; init; }
}

[PublicAPI]
internal sealed class RefreshBrowserSessionEndpoint(RefreshAccessTokenOperation operation,
    IdentityDomainWriteContext context, ITokenService tokenService) : Endpoint<RefreshBrowserSessionRequest, BrowserAuthSessionResponse>
{
    public override void Configure()
    {
        Post("api/browser/auth/refresh");
        // The HttpOnly cookie is the credential; the browser origin guard runs before endpoint binding.
        AllowAnonymous();
        Tags("Identity/Browser");
    }

    public override async Task HandleAsync(RefreshBrowserSessionRequest req, CancellationToken ct)
    {
        var token = BrowserSessionCookie.Read(HttpContext);
        if (string.IsNullOrEmpty(token))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }
        if (req.ExpectedSessionId is { } expected)
        {
            var cookieHash = TokenService.HashToken(token);
            var matches = await context.RefreshTokens.AnyAsync(row => row.TokenHash == cookieHash
                && (row.SessionId ?? row.Id) == expected, ct);
            if (!matches)
            {
                await Send.StatusCodeAsync(StatusCodes.Status409Conflict, ct);
                return;
            }
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
