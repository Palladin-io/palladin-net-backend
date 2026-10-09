using Microsoft.AspNetCore.Http;
using NodaTime;

namespace Palladin.Module.Identity.Infrastructure.BrowserSessions;

internal static class BrowserSessionCookie
{
    internal const string Name = "__Host-palladin-refresh";
    internal static string? Read(HttpContext context) => context.Request.Cookies[Name];

    internal static void Write(HttpContext context, string token, Instant expiresAt) =>
        context.Response.Cookies.Append(Name, token, Options(expiresAt.ToDateTimeOffset()));

    internal static void Delete(HttpContext context) => context.Response.Cookies.Delete(Name, Options(null));

    private static CookieOptions Options(DateTimeOffset? expiresAt) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        Expires = expiresAt,
        IsEssential = true,
    };
}
