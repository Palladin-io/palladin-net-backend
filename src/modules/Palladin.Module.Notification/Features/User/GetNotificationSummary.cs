using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Notification.Infrastructure.Persistence;
using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;

namespace Palladin.Module.Notification.Features;

[PublicAPI]
public sealed record NotificationSummaryResponse(int UnreadCount, int PendingActionCount);

[PublicAPI]
internal sealed class GetNotificationSummaryEndpoint(NotificationDomainReadContext domainReadContext)
    : EndpointWithoutRequest<NotificationSummaryResponse>
{
    public override void Configure()
    {
        Get("api/notifications/summary");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Summary(summary =>
        {
            summary.Summary = "Counts for the navigation badge and To-do header";
            summary.Description = "unreadCount drives the nav badge; pendingActionCount is the number of unresolved action-required items. Both are dumb single-table counts over the user's denormalized inbox — collapse and loss-of-access already removed resolved/inaccessible items at write time, so no scope filter or join is needed.";
        });
        Tags("Notification/Inbox");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var userId = User.GetUserId()!.Value;
        var organizationId = User.GetOrganizationId()!.Value;

        var counts = await domainReadContext.InboxItems
            .Where(i => i.OrganizationId == organizationId && i.UserId == userId)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Unread = g.Count(i => i.ReadAt == null),
                Pending = g.Count(i => i.Category == NotificationCategory.ActionRequired),
            })
            .FirstOrDefaultAsync(ct);

        await Send.OkAsync(new NotificationSummaryResponse(counts?.Unread ?? 0, counts?.Pending ?? 0), ct);
    }
}
