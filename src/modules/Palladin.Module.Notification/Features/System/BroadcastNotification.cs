using Palladin.Core.Guid;
using Palladin.Core.Security;
using Palladin.Module.Notification.Domain;
using Palladin.Module.Notification.Contracts.Commands;
using Palladin.Module.Notification.Contracts.ValueObjects;
using Palladin.Module.Notification.Infrastructure.MassTransit;
using Palladin.Module.Notification.Infrastructure.Persistence;
using Palladin.Module.Notification.Infrastructure.Push;
using Palladin.Module.Notification.Infrastructure.SignalR;
using Palladin.Module.Notification.Shared;
using JetBrains.Annotations;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Palladin.Module.Notification.Features;

[UsedImplicitly]
internal sealed class BroadcastNotificationConsumerDefinition : ConsumerDefinition<BroadcastNotificationConsumer>
{
    public BroadcastNotificationConsumerDefinition() => EndpointName = NotificationEndpoints.Inbox;
}

[UsedImplicitly]
internal sealed class BroadcastNotificationConsumer(
    NotificationDomainWriteContext domainWriteContext,
    IGuidProvider guidProvider,
    IWebNotifier webNotifier,
    IPushNotificationService pushService) : IConsumer<BroadcastNotificationCommand>
{
    private static readonly NotificationScope EmptyScope = new(string.Empty, Guid.Empty);

    public async Task Consume(ConsumeContext<BroadcastNotificationCommand> context)
    {
        var msg = context.Message;
        var ct = context.CancellationToken;

        OpaqueVaultNotificationPolicy.EnsureSafe(msg);
        await CollapsePendingAsync(msg, ct);

        var alreadyDelivered = await domainWriteContext.InboxItems
            .AnyAsync(
                i => i.OrganizationId == msg.OrganizationId
                     && i.Type == msg.Type
                     && i.SubjectId == msg.SubjectId,
                ct);
        if (alreadyDelivered)
        {
            // A terminal event may arrive before its pending notification. Re-delivery must still
            // collapse that late pending row; commit the deletion atomically before deduplicating.
            if (msg.CollapsesPending)
            {
                await domainWriteContext.CommitAsync(ct);
            }

            return;
        }

        if (NotificationDefaults.IsInvisibleMarker(msg.Type))
        {
            await domainWriteContext.CommitAsync(ct);
            return;
        }

        var recipients = await ResolveRecipientsAsync(msg, ct);
        if (recipients.Count == 0)
        {
            await domainWriteContext.CommitAsync(ct);
            return;
        }

        var recipientIds = recipients.Select(r => r.UserId).ToList();
        var preferences = await LoadPreferencesAsync(msg.OrganizationId, msg.Type, recipientIds, ct);

        var realtimeRecipients = new List<Guid>();
        var pushRecipients = new List<Guid>();
        foreach (var (userId, scope) in recipients)
        {
            preferences.TryGetValue(userId, out var stored);
            var effective = NotificationDefaults.Resolve(msg.Type, stored);

            if (effective.Inbox)
            {
                domainWriteContext.Add(InboxItem.Create(
                    guidProvider.Generate(),
                    msg.OrganizationId,
                    userId,
                    msg.Type,
                    msg.Category,
                    msg.TitleKey,
                    msg.Metadata,
                    scope.Type,
                    scope.ItemId,
                    msg.SubjectId,
                    msg.Collapsible,
                    msg.RequiredPermission,
                    msg.OccurredAt));
            }

            if (effective.Realtime)
            {
                realtimeRecipients.Add(userId);
            }

            if (effective.Push)
            {
                pushRecipients.Add(userId);
            }
        }

        await domainWriteContext.CommitAsync(ct);

        if (realtimeRecipients.Count > 0)
        {
            var payload = new NotificationPayload(
                msg.SubjectId, msg.Type, msg.Category, msg.TitleKey, msg.Metadata, msg.OccurredAt);
            await webNotifier.NotifyUsersAsync(msg.OrganizationId, realtimeRecipients, payload, ct);
        }

        if (pushRecipients.Count > 0)
        {
            await pushService.SendToUsersAsync(
                msg.OrganizationId,
                pushRecipients,
                new PushDispatch(msg.SubjectId, msg.Type, msg.Category, msg.OccurredAt),
                ct);
        }
    }

    private async Task<List<(Guid UserId, NotificationScope Scope)>> ResolveRecipientsAsync(
        BroadcastNotificationCommand msg, CancellationToken ct)
    {
        if (msg.Scopes.Count == 0)
        {
            var orgUsers = await GatedOrgUsers(msg).Distinct().ToListAsync(ct);
            return orgUsers.Select(userId => (userId, EmptyScope)).ToList();
        }

        var scopeTypes = msg.Scopes.Select(s => s.Type).Distinct().ToList();
        var itemIds = msg.Scopes.Select(s => s.ItemId).Distinct().ToList();

        var candidates = domainWriteContext.Scopes
            .Where(s => s.OrganizationId == msg.OrganizationId
                        && scopeTypes.Contains(s.Type)
                        && itemIds.Contains(s.ItemId));

        if (msg.RequiredPermission is not null)
        {
            var permitted = GatedOrgUsers(msg);
            candidates = candidates.Where(s => permitted.Contains(s.UserId));
        }

        var matched = await candidates
            .Select(s => new { s.Type, s.ItemId, s.UserId })
            .ToListAsync(ct);

        var usersByScope = matched
            .ToLookup(s => (s.Type, s.ItemId), s => s.UserId);

        var seen = new HashSet<Guid>();
        var recipients = new List<(Guid, NotificationScope)>();
        foreach (var scope in msg.Scopes)
        {
            foreach (var userId in usersByScope[(scope.Type, scope.ItemId)].Where(seen.Add))
            {
                recipients.Add((userId, scope));
            }
        }

        return recipients;
    }

    private IQueryable<Guid> GatedOrgUsers(BroadcastNotificationCommand msg)
    {
        var users = domainWriteContext.Users.Where(u => u.OrganizationId == msg.OrganizationId);
        if (msg.RequiredPermission is { } gate)
        {
            users = users.Where(u => (u.Permissions & gate) != 0);
        }

        return users.Select(u => u.UserId);
    }

    private async Task CollapsePendingAsync(BroadcastNotificationCommand msg, CancellationToken ct)
    {
        if (!msg.CollapsesPending)
        {
            return;
        }

        var pending = await domainWriteContext.InboxItems
            .Where(i => i.OrganizationId == msg.OrganizationId
                        && i.SubjectId == msg.SubjectId
                        && i.Collapsible)
            .ToListAsync(ct);
        if (pending.Count > 0)
        {
            domainWriteContext.RemoveRange(pending);
        }
    }

    private async Task<Dictionary<Guid, NotificationPreference>> LoadPreferencesAsync(
        Guid organizationId,
        Core.Types.NotificationType type,
        IReadOnlyList<Guid> recipients,
        CancellationToken ct) =>
        await domainWriteContext.NotificationPreferences
            .Where(p => p.OrganizationId == organizationId && p.Type == type && recipients.Contains(p.UserId))
            .ToDictionaryAsync(p => p.UserId, ct);
}
