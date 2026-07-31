using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Notification.Infrastructure.Persistence;
using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Palladin.Module.Notification.Features;

// Token value is intentionally NOT returned — it is a device secret. Only management metadata.
[PublicAPI]
public sealed record PushTokenListItem(
    Guid Id,
    PushPlatform Platform,
    string? DeviceName,
    Instant CreatedAt);

[PublicAPI]
public sealed record ListPushTokensResponse(IReadOnlyList<PushTokenListItem> Items);

[PublicAPI]
internal sealed class ListPushTokensEndpoint(NotificationDomainReadContext domainReadContext)
    : EndpointWithoutRequest<ListPushTokensResponse>
{
    public override void Configure()
    {
        Get("api/push-tokens");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Summary(summary =>
        {
            summary.Summary = "List the current user's registered devices";
            summary.Description = "Returns device-management metadata for the current user's push tokens. The raw token value is never returned.";
        });
        Tags("Notification/PushTokens");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var userId = User.GetUserId()!.Value;

        var items = await domainReadContext.PushTokens
            .Where(t => t.UserId == userId)
            .OrderBy(t => t.CreatedAt)
            .Select(t => new PushTokenListItem(t.Id, t.Platform, t.DeviceName, t.CreatedAt))
            .ToListAsync(ct);

        await Send.OkAsync(new ListPushTokensResponse(items), ct);
    }
}
