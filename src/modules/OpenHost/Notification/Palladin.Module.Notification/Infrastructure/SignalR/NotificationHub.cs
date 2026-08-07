using Palladin.Core.Security;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Palladin.Module.Notification.Infrastructure.SignalR;

[PublicAPI]
[Authorize]
public sealed class NotificationHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        var organizationId = Context.User?.GetOrganizationId();
        if (organizationId is not null)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, NotificationGroups.Organization(organizationId.Value));
        }

        var userId = Context.User?.GetUserId();
        if (userId is not null)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, NotificationGroups.User(userId.Value));
        }

        await base.OnConnectedAsync();
    }
}
