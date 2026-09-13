using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using Palladin.Core.Security;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure.Consents;
using Palladin.Module.Identity.Infrastructure.Persistence;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record GetUserConsentsRequest
{
    public string Locale { get; init; } = "en";
}

[UsedImplicitly]
internal sealed class GetUserConsentsValidator : Validator<GetUserConsentsRequest>
{
    public GetUserConsentsValidator() => RuleFor(request => request.Locale).Must(locale => locale is "pl" or "en");
}

[PublicAPI]
public sealed record UserConsentNoticeResponse(string Version, string Locale, string Text);

[PublicAPI]
public sealed record UserConsentResponse(
    string Purpose, string Scope, string Status, uint Revision, uint ActivationRevision, Instant? RecordedAt,
    string? NoticeVersion, string? NoticeLocale, UserConsentNoticeResponse? CurrentNotice);

[PublicAPI]
public sealed record UserConsentsResponse(UserConsentResponse[] Consents, int MaxAgeSeconds);

internal static class UserConsentResponses
{
    internal static UserConsentResponse Create(string purpose, UserConsent? consent, ConsentNotice? notice) => new(
        purpose, ConsentPurpose.Scope(purpose), consent?.Status ?? "unknown", consent?.Revision ?? 0,
        consent?.ActivationRevision ?? 0, consent?.RecordedAt, consent?.NoticeVersion, consent?.Locale,
        notice is null ? null : new UserConsentNoticeResponse(notice.Version, notice.Locale, notice.Text));
}

[PublicAPI]
internal sealed class GetUserConsentsEndpoint(
    IdentityDomainReadContext context, ConsentNoticeCatalog notices, IOptions<ConsentOptions> options)
    : Endpoint<GetUserConsentsRequest, UserConsentsResponse>
{
    public override void Configure()
    {
        Get("api/account/consents");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Tags("Identity/Account");
        Summary(summary => summary.Summary = "Get the current user's optional consent decisions and available notices");
    }

    public override async Task HandleAsync(GetUserConsentsRequest req, CancellationToken ct)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        var userId = User.GetUserId();
        if (userId is null || !await context.Users.AnyAsync(user => user.Id == userId.Value, ct))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var consents = await context.UserConsents.Where(consent => consent.UserId == userId.Value).ToListAsync(ct);
        string[] purposes = [ConsentPurpose.ProductAnalytics, ConsentPurpose.EmailMarketing];
        await Send.OkAsync(new UserConsentsResponse(purposes.Select(purpose => UserConsentResponses.Create(
            purpose, consents.SingleOrDefault(consent => consent.Purpose == purpose), notices.Current(purpose, req.Locale)))
            .ToArray(), options.Value.MaxAgeSeconds), ct);
    }
}
