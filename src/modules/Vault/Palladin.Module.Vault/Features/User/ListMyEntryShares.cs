using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Api;
using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Infrastructure.Sharing;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record ListMyEntrySharesRequest
{
    public string? Cursor { get; init; }
    public int PageSize { get; init; } = 20;
}

[PublicAPI]
public sealed record MyEntryShareListItem(Guid VaultId, Guid EntryId, EntryShareListItem Share);

[PublicAPI]
public sealed record ListMyEntrySharesResponse(IReadOnlyList<MyEntryShareListItem> Items, string? NextCursor);

[UsedImplicitly]
internal sealed class ListMyEntrySharesValidator : Validator<ListMyEntrySharesRequest>
{
    public ListMyEntrySharesValidator()
    {
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
        RuleFor(x => x.Cursor).MaximumLength(128);
    }
}

[PublicAPI]
internal sealed class ListMyEntrySharesEndpoint(
    VaultDomainReadContext context, EntryShareSecurity security, IClock clock)
    : Endpoint<ListMyEntrySharesRequest, ListMyEntrySharesResponse>
{
    public override void Configure()
    {
        Get("api/entry-sharing");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.VaultManage);
        this.RequireEmailVerified();
        Tags("Vault/Sharing");
        Summary(s => s.Summary = "List the current Member's sharing links without exposing bearer tokens");
    }

    public override async Task HandleAsync(ListMyEntrySharesRequest req, CancellationToken ct)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        var organizationId = User.GetOrganizationId()!.Value;
        var userId = User.GetUserId()!.Value;
        var revokedThrough = await context.EntryShareSenderAuthorities.Where(x =>
            x.OrganizationId == organizationId && x.UserId == userId)
            .Select(x => (uint?)x.RevokedThroughAuthorizationVersion).SingleOrDefaultAsync(ct);
        var organizationBlocked = await context.VaultOrganizationLifecycles.AnyAsync(x =>
            x.OrganizationId == organizationId && x.SharingDisabled, ct);
        var query = from share in context.EntryShares
                    join member in context.VaultMembers on
                        new { share.OrganizationId, share.VaultId, UserId = share.CreatedBy } equals
                        new { member.OrganizationId, member.VaultId, member.UserId }
                    join source in context.Entries on
                        new { share.OrganizationId, share.VaultId, Id = share.EntryId } equals
                        new { source.OrganizationId, source.VaultId, source.Id }
                    where share.OrganizationId == organizationId && share.CreatedBy == userId
                    select new { Share = share, MemberAddedAt = member.AddedAt,
                        source.CurrentRevision, source.State, source.SharingRevokedThroughRevision };
        var cursor = InstantCursor.Decode(req.Cursor);
        if (cursor is not null)
        {
            query = query.Where(x => x.Share.CreatedAt > cursor.Timestamp
                || (x.Share.CreatedAt == cursor.Timestamp && x.Share.Id.CompareTo(cursor.Id) > 0));
        }

        var rows = await query.OrderBy(x => x.Share.CreatedAt).ThenBy(x => x.Share.Id).Take(req.PageSize + 1)
            .Select(x => new
            {
                x.Share.Id, x.Share.CreatedAt, x.Share.ExpiresAt, x.Share.MaximumReceipts, x.Share.DeliveryCount, x.Share.FirstDeliveredAt,
                x.Share.LastDeliveredAt, x.Share.FirstConfirmedAt, x.Share.NotifyOnFirstReceipt, x.Share.RecipientMode,
                x.Share.ProtectedRecipientEmail, x.Share.Protection, x.Share.SourceRevision, x.Share.RevokedAt, x.Share.LockedUntil,
                x.Share.SenderAuthorizationVersion, x.Share.SenderVaultMembershipAddedAt,
                x.Share.VaultId, x.Share.EntryId, x.MemberAddedAt, x.CurrentRevision, x.State, x.SharingRevokedThroughRevision,
            }).ToListAsync(ct);
        var hasNext = rows.Count > req.PageSize;
        var now = clock.GetCurrentInstant();
        var items = rows.Take(req.PageSize).Select(x => new MyEntryShareListItem(x.VaultId, x.EntryId, new EntryShareListItem(
            x.Id,
            x.RevokedAt is not null ? "revoked"
                : x.ExpiresAt <= now ? "expired"
                : organizationBlocked || revokedThrough is null || x.SenderAuthorizationVersion <= revokedThrough
                  || x.SenderVaultMembershipAddedAt != x.MemberAddedAt
                  || x.SharingRevokedThroughRevision >= x.SourceRevision.Value ? "revoked"
                : x.State != EntryState.Active ? "suspended"
                : x.LockedUntil > now ? "locked"
                : x.DeliveryCount >= x.MaximumReceipts ? "consumed" : "active",
            x.CreatedAt, x.ExpiresAt, x.MaximumReceipts, x.DeliveryCount, x.FirstDeliveredAt, x.LastDeliveredAt,
            x.FirstConfirmedAt, x.NotifyOnFirstReceipt, x.RecipientMode,
            x.ProtectedRecipientEmail is null ? null : security.UnprotectRecipientEmail(x.Id, x.ProtectedRecipientEmail),
            x.Protection, x.SourceRevision != x.CurrentRevision))).ToList();
        await Send.OkAsync(new ListMyEntrySharesResponse(items,
            hasNext ? InstantCursor.Encode(rows[req.PageSize - 1].CreatedAt, rows[req.PageSize - 1].Id) : null), ct);
    }
}
