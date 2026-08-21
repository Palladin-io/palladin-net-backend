using Palladin.Core.Security;
using Palladin.Module.Notification.Infrastructure.Persistence;
using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;

namespace Palladin.Module.Notification.Features;

[PublicAPI]
public sealed record MarkAllNotificationsReadResponse(int MarkedCount);

[PublicAPI]
[AllowNonActiveOrganizationMembership]
internal sealed class MarkAllNotificationsReadEndpoint(
    NotificationDomainWriteContext domainWriteContext,
    NodaTime.IClock clock) : EndpointWithoutRequest<MarkAllNotificationsReadResponse>
{
    public override void Configure()
    {
        Put("api/notifications/read-all");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Summary(summary =>
        {
            summary.Summary = "Mark all unread notifications as read";
            summary.Description = "Marks every unread inbox item of the current user as read. Returns how many were marked.";
        });
        Tags("Notification/Inbox");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var userId = User.GetUserId()!.Value;
        var organizationId = User.GetOrganizationId()!.Value;
        var now = clock.GetCurrentInstant();

        var unread = await domainWriteContext.InboxItems
            .Where(i => i.OrganizationId == organizationId && i.UserId == userId && i.ReadAt == null)
            .ToListAsync(ct);

        foreach (var item in unread)
        {
            item.MarkRead(now);
        }

        await domainWriteContext.CommitAsync(ct);

        await Send.OkAsync(new MarkAllNotificationsReadResponse(unread.Count), ct);
    }
}
