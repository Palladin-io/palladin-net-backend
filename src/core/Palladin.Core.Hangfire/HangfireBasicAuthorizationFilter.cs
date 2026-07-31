using Hangfire.Dashboard;
using Palladin.Core.Api;

namespace Palladin.Core.Hangfire;

internal sealed class HangfireBasicAuthorizationFilter(string login, string password) : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();

        if (BasicAuthValidator.Validate(httpContext, login, password))
        {
            return true;
        }

        BasicAuthValidator.SetChallenge(httpContext, "Hangfire Dashboard");
        return false;
    }
}
