using FastEndpoints;

namespace Palladin.Core.Security;

public sealed class RequirePermissionPreProcessor<TRequest>(Permission required) : IPreProcessor<TRequest>
{
    public async Task PreProcessAsync(IPreProcessorContext<TRequest> context, CancellationToken ct)
    {
        if (required == Permission.None)
        {
            return;
        }

        var user = context.HttpContext.User;
        if (user.GetUserId() is null || user.GetOrganizationId() is null)
        {
            await context.HttpContext.Response.SendUnauthorizedAsync(ct);
            return;
        }

        var granted = user.GetPermissions();
        if ((granted & required) != required)
        {
            await context.HttpContext.Response.SendForbiddenAsync(ct);
        }
    }
}
