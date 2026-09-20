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
public sealed record ListEntrySharesRequest
{
    public Guid VaultId { get; init; }
    public Guid EntryId { get; init; }
    public string? Cursor { get; init; }
    public int PageSize { get; init; } = 20;
}

[PublicAPI]
public sealed record EntryShareListItem(
    Guid ShareId, string Status, Instant CreatedAt, Instant ExpiresAt,
    int MaximumReceipts, int DeliveryCount, Instant? FirstDeliveredAt,
    Instant? LastDeliveredAt, Instant? FirstConfirmedAt, bool NotifyOnFirstReceipt,
    EntryShareRecipientMode RecipientMode, string? RecipientEmail,
    EntryShareProtection Protection, bool SourceChanged);

[PublicAPI]
public sealed record ListEntrySharesResponse(IReadOnlyList<EntryShareListItem> Items, string? NextCursor);

[UsedImplicitly]
internal sealed class ListEntrySharesValidator : Validator<ListEntrySharesRequest>
{
    public ListEntrySharesValidator()
    {
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.EntryId).NotEmpty();
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
        RuleFor(x => x.Cursor).MaximumLength(128);
    }
}

[PublicAPI]
internal sealed class ListEntrySharesEndpoint(
    VaultDomainReadContext context, EntryShareSecurity security, IClock clock)
    : Endpoint<ListEntrySharesRequest, ListEntrySharesResponse>
{
    public override void Configure()
    {
        Get("api/vaults/{vaultId:guid}/entries/{entryId:guid}/sharing");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.VaultManage);
        this.RequireEmailVerified();
        Tags("Vault/Sharing");
        Summary(s => s.Summary = "List the current Member's sharing links without exposing bearer tokens");
    }

    public override async Task HandleAsync(ListEntrySharesRequest req, CancellationToken ct)
   {
        HttpContext.Response.Headers.CacheControl = "no-store";
        var organizationId = User.GetOrganizationId()!.Value;
        var userId = User.GetUserId()!.Value;
        var member = await context.VaultMembers.Where(x => x.OrganizationId == organizationId
            && x.VaultId == req.VaultId && x.UserId == userId).Select(x => new { x.AddedAt }).SingleOrDefaultAsync(ct);
        var source = await context.Entries.Where(x => x.OrganizationId == organizationId
            && x.VaultId == req.VaultId && x.Id == req.EntryId)
            .Select(x => new { x.CurrentRevision, x.State, x.SharingRevokedThroughRevision }).SingleOrDefaultAsync(ct);
        if (member is null || source is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var revokedThrough = await context.EntryShareSenderAuthorities.Where(x =>
            x.OrganizationId == organizationId && x.UserId == userId)
            .Select(x => (uint?)x.RevokedThroughAuthorizationVersion).SingleOrDefaultAsync(ct);
        var organizationBlocked = await context.VaultOrganizationLifecycles.AnyAsync(x =>
            x.OrganizationId == organizationId && x.SharingDisabled, ct);
        var query = context.EntryShares.Where(x => x.OrganizationId == organizationId && x.VaultId == req.VaultId
            && x.EntryId == req.EntryId && x.CreatedBy == userId);
        var cursor = InstantCursor.Decode(req.Cursor);
        if (cursor is not null)
        {
            query = query.Where(x => x.CreatedAt > cursor.Timestamp
                || (x.CreatedAt == cursor.Timestamp && x.Id.CompareTo(cursor.Id) > 0));
        }

        var rows = await query.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Take(req.PageSize + 1)
            .Select(x => new
            {
                x.Id, x.CreatedAt, x.ExpiresAt, x.MaximumReceipts, x.DeliveryCount, x.FirstDeliveredAt,
                x.LastDeliveredAt, x.FirstConfirmedAt, x.NotifyOnFirstReceipt, x.RecipientMode,
                x.ProtectedRecipientEmail, x.Protection, x.SourceRevision, x.RevokedAt, x.LockedUntil,
                x.SenderAuthorizationVersion, x.SenderVaultMembershipAddedAt,
            }).ToListAsync(ct);
        var hasNext = rows.Count > req.PageSize;
        var now = clock.GetCurrentInstant();
        var items = rows.Take(req.PageSize).Select(x => new EntryShareListItem(
            x.Id,
            x.RevokedAt is not null ? "revoked"
                : x.ExpiresAt <= now ? "expired"
                : organizationBlocked || revokedThrough is null || x.SenderAuthorizationVersion <= revokedThrough
                  || x.SenderVaultMembershipAddedAt != member.AddedAt
                  || source.SharingRevokedThroughRevision >= x.SourceRevision.Value ? "revoked"
                : source.State != EntryState.Active ? "suspended"
                : x.LockedUntil > now ? "locked"
                : x.DeliveryCount >= x.MaximumReceipts ? "consumed" : "active",
            x.CreatedAt, x.ExpiresAt, x.MaximumReceipts, x.DeliveryCount, x.FirstDeliveredAt, x.LastDeliveredAt,
            x.FirstConfirmedAt, x.NotifyOnFirstReceipt, x.RecipientMode,
            x.ProtectedRecipientEmail is null ? null : security.UnprotectRecipientEmail(x.Id, x.ProtectedRecipientEmail),
            x.Protection, x.SourceRevision != source.CurrentRevision)).ToList();
        await Send.OkAsync(new ListEntrySharesResponse(items,
            hasNext ? InstantCursor.Encode(rows[req.PageSize - 1].CreatedAt, rows[req.PageSize - 1].Id) : null), ct);
    }
}
