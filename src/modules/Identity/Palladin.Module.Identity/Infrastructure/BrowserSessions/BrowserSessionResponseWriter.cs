using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Palladin.Module.Identity.Features;
using Palladin.Module.Identity.Infrastructure.Jwt;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Shared;

namespace Palladin.Module.Identity.Infrastructure.BrowserSessions;

internal sealed class BrowserSessionResponseWriter(IdentityDomainWriteContext context, ITokenService tokenService)
{
    internal async Task<IdentityOperationResult<object>> MapAsync<T>(IdentityOperationResult<T> result,
        HttpContext httpContext, CancellationToken ct)
    {
        if (result.Body is null)
        {
            return new(result.StatusCode, Error: result.Error, RetryAfterSeconds: result.RetryAfterSeconds);
        }
        if (result.Body is LoginResponse { TotpRequired: true } challenge)
        {
            return IdentityOperationResult<object>.Ok(new { totpRequired = true, challengeToken = challenge.ChallengeToken });
        }
        var session = result.Body switch
        {
            AuthSessionResponse value => value,
            LoginResponse value => new AuthSessionResponse(value.AccessToken!, value.RefreshToken!, value.UserId!.Value,
                value.IsOnboarded!.Value, value.EmailVerified!.Value, value.WaitlistDeveloperBenefitStartedAt, value.WaitlistDeveloperBenefitEndsAt),
            OAuthAuthenticateResponse value => new AuthSessionResponse(value.AccessToken, value.RefreshToken, value.UserId,
                value.IsOnboarded, value.EmailVerified, value.WaitlistDeveloperBenefitStartedAt, value.WaitlistDeveloperBenefitEndsAt),
            RefreshAccessTokenResponse value => new AuthSessionResponse(value.AccessToken, value.RefreshToken, value.UserId,
                value.IsOnboarded, value.EmailVerified, value.WaitlistDeveloperBenefitStartedAt, value.WaitlistDeveloperBenefitEndsAt),
            _ => throw new InvalidOperationException("Unsupported session response."),
        };
        var hash = TokenService.HashToken(session.RefreshToken);
        var token = await context.RefreshTokens.SingleAsync(token => token.TokenHash == hash, ct);
        BrowserSessionCookie.Write(httpContext, session.RefreshToken, token.ExpiresAt);
        return IdentityOperationResult<object>.Ok(new BrowserAuthSessionResponse(tokenService.BindBrowserSession(session.AccessToken, token.SessionId ?? token.Id),
            token.SessionId ?? token.Id, session.UserId, session.IsOnboarded, session.EmailVerified,
            session.WaitlistDeveloperBenefitStartedAt, session.WaitlistDeveloperBenefitEndsAt,
            result.Body is OAuthAuthenticateResponse { IsNewUser: true }));
    }
}
