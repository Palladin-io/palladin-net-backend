using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Notification.Domain;
using Palladin.Module.Notification.Infrastructure.Persistence;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Palladin.Module.Notification.Features;

[PublicAPI]
public sealed record UpdateNotificationPreferenceItem
{
    public NotificationType Type { get; init; }
    public bool InboxEnabled { get; init; }
    public bool SignalREnabled { get; init; }
    public bool PushEnabled { get; init; }
}

[PublicAPI]
public sealed record UpdateNotificationPreferencesRequest
{
    public IReadOnlyList<UpdateNotificationPreferenceItem> Items { get; init; } = [];
}

[PublicAPI]
public sealed record UpdateNotificationPreferencesResponse(IReadOnlyList<PreferenceItem> Items);

[UsedImplicitly]
internal sealed class UpdateNotificationPreferencesValidator : Validator<UpdateNotificationPreferencesRequest>
{
    public UpdateNotificationPreferencesValidator()
    {
        RuleFor(x => x.Items).NotNull();
        RuleForEach(x => x.Items).ChildRules(item =>
            item.RuleFor(i => i.Type).IsInEnum());
    }
}

[PublicAPI]
internal sealed class UpdateNotificationPreferencesEndpoint(
    NotificationDomainWriteContext domainWriteContext,
    IClock clock) : Endpoint<UpdateNotificationPreferencesRequest, UpdateNotificationPreferencesResponse>
{
    public override void Configure()
    {
        Put("api/notifications/preferences");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Options(builder => builder.AllowNonActiveOrganizationMembership());
        Summary(summary =>
        {
            summary.Summary = "Update the current user's notification preferences";
            summary.Description = "Upserts the supplied per-type channel preferences. For mandatory types the inbox and realtime channels are forced on regardless of the request; only pushEnabled is honoured. Returns the full effective preference set.";
        });
        Tags("Notification/Preferences");
    }

    public override async Task HandleAsync(UpdateNotificationPreferencesRequest req, CancellationToken ct)
    {
        var userId = User.GetUserId()!.Value;
        var organizationId = User.GetOrganizationId()!.Value;
        var now = clock.GetCurrentInstant();

        var existing = await domainWriteContext.NotificationPreferences
            .Where(p => p.OrganizationId == organizationId && p.UserId == userId)
            .ToDictionaryAsync(p => p.Type, ct);

        foreach (var item in req.Items.GroupBy(i => i.Type).Select(g => g.Last()))
        {
            var mandatory = NotificationDefaults.IsMandatory(item.Type);
            var inbox = mandatory || item.InboxEnabled;
            var signalR = mandatory || item.SignalREnabled;

            if (existing.TryGetValue(item.Type, out var pref))
            {
                pref.Update(inbox, signalR, item.PushEnabled, now);
            }
            else
            {
                var created = NotificationPreference.Create(
                    organizationId, userId, item.Type, inbox, signalR, item.PushEnabled, now);
                domainWriteContext.Add(created);
                existing[item.Type] = created;
            }
        }

        await domainWriteContext.CommitAsync(ct);

        await Send.OkAsync(new UpdateNotificationPreferencesResponse(PreferenceItemFactory.BuildAll(existing)), ct);
    }
}
