using Microsoft.AspNetCore.Http;

namespace Palladin.Module.Identity.Infrastructure.BrowserSessions;

internal static class BrowserSessionOriginGuard
{
    internal static bool IsAllowed(HttpContext context, IReadOnlyCollection<string> allowedOrigins)
    {
        var origin = context.Request.Headers.Origin;
        var header = context.Request.Headers["X-Palladin-Browser"];
        return origin.Count == 1 && header.Count == 1 && header[0] == "1"
            && allowedOrigins.Contains(origin[0], StringComparer.Ordinal);
    }
}
