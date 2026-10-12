using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Palladin.Core.Security;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure.Jwt;

namespace Palladin.Module.Identity.Infrastructure.BrowserSessions;

internal static class BrowserOwnSession
{
    internal static async Task<string?> ResolveAsync(HttpContext httpContext, Guid expectedSessionId,
        IQueryable<RefreshToken> tokens, CancellationToken ct)
    {
        var user = httpContext.User;
        var raw = BrowserSessionCookie.Read(httpContext);
        if (raw is null || expectedSessionId == Guid.Empty || user.GetBrowserSessionId() != expectedSessionId)
        {
            return null;
        }
        var hash = TokenService.HashToken(raw);
        return await tokens.AnyAsync(token => token.TokenHash == hash
            && token.UserId == user.GetUserId() && token.OrganizationId == user.GetOrganizationId()
            && (token.SessionId ?? token.Id) == expectedSessionId, ct) ? raw : null;
    }
}
