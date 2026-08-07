using Palladin.Core.Security;
using Palladin.Module.Notification.Infrastructure.Persistence;
using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;

namespace Palladin.Module.Notification.Features;

[PublicAPI]
public sealed record GetNotificationPreferencesResponse(IReadOnlyList<PreferenceItem> Items);

[PublicAPI]
internal sealed class GetNotificationPreferencesEndpoint(NotificationDomainReadContext domainReadContext)
    : EndpointWithoutRequest<GetNotificationPreferencesResponse>
{
    public override void Configure()
    {
        Get("api/notifications/preferences");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Summary(summary =>
        {
            summary.Summary = "Get the current user's notification preferences";
            summary.Description = "One item per notification type with the effective channel state (stored deviations merged over defaults, mandatory locks applied). mandatory=true means inbox+realtime are forced on; only push is changeable.";
        });
        Tags("Notification/Preferences");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var userId = User.GetUserId()!.Value;
        var organizationId = User.GetOrganizationId()!.Value;

        var stored = await domainReadContext.NotificationPreferences
            .Where(p => p.OrganizationId == organizationId && p.UserId == userId)
            .ToDictionaryAsync(p => p.Type, ct);

        await Send.OkAsync(new GetNotificationPreferencesResponse(PreferenceItemFactory.BuildAll(stored)), ct);
    }
}
