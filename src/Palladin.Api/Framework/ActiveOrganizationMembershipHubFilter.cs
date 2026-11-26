using Microsoft.AspNetCore.SignalR;
using Palladin.Core.Security;
using Palladin.Module.Identity.Shared;

namespace Palladin.Api.Framework;

// Checked after the hub registers the connection, so a removal committed after this check still
// reaches it through the disconnect command.
internal sealed class ActiveOrganizationMembershipHubFilter : IHubFilter
{
    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        await next(context);

        var user = context.Context.User;
        var userId = user?.GetUserId();
        var organizationId = user?.GetOrganizationId();
        var authorizationVersion = user?.GetAuthorizationVersion();
        if (userId is null || organizationId is null || authorizationVersion is null)
        {
            context.Context.Abort();
            return;
        }

        var validator = context.ServiceProvider.GetRequiredService<IOrganizationMembershipValidator>();
        if (!await validator.IsActiveAsync(
                userId.Value,
                organizationId.Value,
                authorizationVersion.Value,
                context.Context.ConnectionAborted))
        {
            context.Context.Abort();
        }
    }
}
