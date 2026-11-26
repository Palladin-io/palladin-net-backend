using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

namespace Palladin.Module.Notification.Infrastructure.SignalR;

internal sealed class RealtimeConnectionRegistry
{
    private readonly ConcurrentDictionary<(Guid OrganizationId, Guid UserId), ConcurrentDictionary<string, (HubCallerContext Context, uint AuthorizationVersion)>> connections = new();

    internal void Add(Guid organizationId, Guid userId, uint authorizationVersion, HubCallerContext context) =>
        connections.GetOrAdd((organizationId, userId), _ => new())[context.ConnectionId] = (context, authorizationVersion);

    internal void Remove(Guid organizationId, Guid userId, string connectionId)
    {
        if (connections.TryGetValue((organizationId, userId), out var userConnections))
        {
            userConnections.TryRemove(connectionId, out _);
        }
    }

    // A rejoined Member always receives a higher authorization version, so a delayed removal never
    // closes connections authorized after it.
    internal void Abort(Guid organizationId, Guid userId, uint removedAuthorizationVersion)
    {
        if (!connections.TryGetValue((organizationId, userId), out var userConnections))
        {
            return;
        }

        foreach (var (context, authorizationVersion) in userConnections.Values)
        {
            if (authorizationVersion <= removedAuthorizationVersion)
            {
                context.Abort();
            }
        }
    }
}
