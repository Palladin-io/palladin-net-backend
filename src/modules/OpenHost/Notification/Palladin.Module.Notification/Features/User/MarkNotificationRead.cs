using Palladin.Core.Security;
using Palladin.Module.Notification.Infrastructure.Persistence;
using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Palladin.Module.Notification.Features;

[PublicAPI]
internal sealed class MarkNotificationReadEndpoint(
    NotificationDomainWriteContext domainWriteContext,
    IClock clock) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Put("api/notifications/{id:guid}/read");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Summary(summary =>
        {
            summary.Summary = "Mark a notification as read";
            summary.Description = "Takes no body. Idempotent: marking an already-read item keeps the original read time. 404 when the current user has no inbox item for the id.";
        });
        Tags("Notification/Inbox");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var id = Route<Guid>("id");
        var userId = User.GetUserId()!.Value;
        var organizationId = User.GetOrganizationId()!.Value;

        var item = await domainWriteContext.InboxItems
            .FirstOrDefaultAsync(
                i => i.OrganizationId == organizationId && i.UserId == userId && i.Id == id,
                ct);
        if (item is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        item.MarkRead(clock.GetCurrentInstant());
        await domainWriteContext.CommitAsync(ct);

        await Send.NoContentAsync(ct);
    }
}
