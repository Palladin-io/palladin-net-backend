using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Security;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure.Persistence;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record GetUserConsentHistoryRequest
{
    public string Purpose { get; init; } = string.Empty;
    public uint? BeforeRevision { get; init; }
}

[UsedImplicitly]
internal sealed class GetUserConsentHistoryValidator : Validator<GetUserConsentHistoryRequest>
{
    public GetUserConsentHistoryValidator() => RuleFor(request => request.Purpose)
        .Must(purpose => purpose is ConsentPurpose.ProductAnalytics or ConsentPurpose.EmailMarketing);
}

[PublicAPI]
public sealed record UserConsentHistoryItem(
    string Purpose, string Scope, string Status, uint Revision, Instant RecordedAt,
    string NoticeVersion, string NoticeText, string Locale, string Source);

[PublicAPI]
public sealed record UserConsentHistoryResponse(UserConsentHistoryItem[] Items, uint? NextBeforeRevision);

[PublicAPI]
internal sealed class GetUserConsentHistoryEndpoint(IdentityDomainReadContext context)
    : Endpoint<GetUserConsentHistoryRequest, UserConsentHistoryResponse>
{
    public override void Configure()
    {
        Get("api/account/consents/{Purpose}/history");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Tags("Identity/Account");
        Summary(summary => summary.Summary = "Export the current user's consent history in pages of up to 50 decisions");
    }

    public override async Task HandleAsync(GetUserConsentHistoryRequest req, CancellationToken ct)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        var userId = User.GetUserId();
        if (userId is null || !await context.Users.AnyAsync(user => user.Id == userId.Value, ct))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var query = context.UserConsentHistory.Where(value => value.UserId == userId.Value && value.Purpose == req.Purpose);
        if (req.BeforeRevision is { } before)
        {
            query = query.Where(value => value.Revision < before);
        }

        var items = await query.OrderByDescending(value => value.Revision).Take(51)
            .Select(value => new UserConsentHistoryItem(value.Purpose, value.Scope, value.Status,
                value.Revision, value.RecordedAt, value.NoticeVersion, value.NoticeText, value.Locale, value.Source))
            .ToArrayAsync(ct);
        await Send.OkAsync(new UserConsentHistoryResponse(items.Take(50).ToArray(), items.Length > 50 ? items[49].Revision : null), ct);
    }
}
