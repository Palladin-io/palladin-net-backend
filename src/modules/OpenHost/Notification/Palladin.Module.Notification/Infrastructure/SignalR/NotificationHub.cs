using Palladin.Core.Security;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace Palladin.Module.Notification.Infrastructure.SignalR;

[PublicAPI]
[Authorize]
public sealed class NotificationHub : Hub
{
    private RealtimeConnectionRegistry Registry =>
        Context.GetHttpContext()!.RequestServices.GetRequiredService<RealtimeConnectionRegistry>();

    public override async Task OnConnectedAsync()
    {
        var organizationId = Context.User?.GetOrganizationId();
        var userId = Context.User?.GetUserId();
        if (organizationId is not null && userId is not null)
        {
            Registry.Add(organizationId.Value, userId.Value, Context);
        }

        if (organizationId is not null)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, NotificationGroups.Organization(organizationId.Value));
        }

        if (userId is not null)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, NotificationGroups.User(userId.Value));
        }

        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        var organizationId = Context.User?.GetOrganizationId();
        var userId = Context.User?.GetUserId();
        if (organizationId is not null && userId is not null)
        {
            Registry.Remove(organizationId.Value, userId.Value, Context.ConnectionId);
        }

        return base.OnDisconnectedAsync(exception);
    }
}
