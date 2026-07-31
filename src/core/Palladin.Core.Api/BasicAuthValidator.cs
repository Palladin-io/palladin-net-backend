using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Palladin.Core.Api;

public static class BasicAuthValidator
{
    public static bool Validate(HttpContext context, string login, string password)
    {
        if (!context.Request.Headers.TryGetValue("Authorization", out var authorizationHeader) ||
            !AuthenticationHeaderValue.TryParse(authorizationHeader, out var authValues) ||
            !"Basic".Equals(authValues.Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string parameter;
        try
        {
            parameter = Encoding.UTF8.GetString(Convert.FromBase64String(authValues.Parameter!));
        }
        catch (FormatException)
        {
            return false;
        }

        var parts = parameter.Split(':', 2);
        return parts.Length == 2 && login == parts[0] && password == parts[1];
    }

    public static void SetChallenge(HttpContext context, string realm)
    {
        context.Response.StatusCode = 401;
        context.Response.Headers.Append("WWW-Authenticate", $"Basic realm=\"{realm}\"");
    }
}
