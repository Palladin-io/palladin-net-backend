using Palladin.Core.Api;
using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Notification.Infrastructure.Persistence;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Palladin.Module.Notification.Features;

[PublicAPI]
public sealed record ListNotificationsRequest
{
    public string? Cursor { get; init; }
    public int? Limit { get; init; }
    public NotificationCategory? Category { get; init; }
    public bool? UnreadOnly { get; init; }
}

[PublicAPI]
public sealed record NotificationItem(
    Guid Id,
    Guid SubjectId,
    NotificationType Type,
    NotificationCategory Category,
    string TitleKey,
    IReadOnlyDictionary<string, string> Metadata,
    Instant OccurredAt,
    Instant? ReadAt,
    string? ActionState);

[PublicAPI]
public sealed record ListNotificationsResponse(IReadOnlyList<NotificationItem> Items, string? NextCursor);

[UsedImplicitly]
internal sealed class ListNotificationsValidator : Validator<ListNotificationsRequest>
{
    public ListNotificationsValidator()
    {
        RuleFor(x => x.Limit!.Value).InclusiveBetween(1, 50).When(x => x.Limit is not null);
        RuleFor(x => x.Category!.Value).IsInEnum().When(x => x.Category is not null);
    }
}

[PublicAPI]
internal sealed class ListNotificationsEndpoint(NotificationDomainReadContext domainReadContext)
    : Endpoint<ListNotificationsRequest, ListNotificationsResponse>
{
    private const int DefaultLimit = 20;
    private const int MaxLimit = 50;

    public override void Configure()
    {
        Get("api/notifications");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Summary(summary =>
        {
            summary.Summary = "List the current user's notification inbox";
            summary.Description = "Self-scoped inbox feed, newest first by (occurredAt, id). Filter by category and unread state. Canonical Vault items carry an opaque subject ID, structural metadata, read state and action state; presentation is resolved locally after unlock. Visibility, collapse and loss-of-access are resolved at write time, so the read remains a tenant- and user-scoped single-table projection.";
        });
        Tags("Notification/Inbox");
    }

    public override async Task HandleAsync(ListNotificationsRequest req, CancellationToken ct)
    {
        var userId = User.GetUserId()!.Value;
        var organizationId = User.GetOrganizationId()!.Value;
        var limit = Math.Min(req.Limit ?? DefaultLimit, MaxLimit);
        var cursor = InstantCursor.Decode(req.Cursor);

        var query = domainReadContext.InboxItems
            .Where(i => i.OrganizationId == organizationId && i.UserId == userId);

        if (req.Category is not null)
        {
            query = query.Where(i => i.Category == req.Category);
        }

        if (req.UnreadOnly == true)
        {
            query = query.Where(i => i.ReadAt == null);
        }

        if (cursor is not null)
        {
            query = query.Where(i =>
                i.OccurredAt < cursor.Timestamp
                || (i.OccurredAt == cursor.Timestamp && i.Id.CompareTo(cursor.Id) < 0));
        }

        var rows = await query
            .OrderByDescending(i => i.OccurredAt)
            .ThenByDescending(i => i.Id)
            .Take(limit + 1)
            .Select(i => new
            {
                i.Id,
                i.SubjectId,
                i.Type,
                i.Category,
                i.TitleKey,
                i.Metadata,
                i.OccurredAt,
                i.ReadAt,
            })
            .ToListAsync(ct);

        string? nextCursor = null;
        if (rows.Count > limit)
        {
            var last = rows[limit - 1];
            nextCursor = InstantCursor.Encode(last.OccurredAt, last.Id);
            rows = rows.Take(limit).ToList();
        }

        var items = rows
            .Select(r => new NotificationItem(
                r.Id,
                r.SubjectId,
                r.Type,
                r.Category,
                r.TitleKey,
                r.Metadata,
                r.OccurredAt,
                r.ReadAt,
                ResolveActionState(r.Category)))
            .ToList();

        await Send.OkAsync(new ListNotificationsResponse(items, nextCursor), ct);
    }

    private static string? ResolveActionState(NotificationCategory category) =>
        category == NotificationCategory.ActionRequired ? NotificationActionState.Pending : null;
}
