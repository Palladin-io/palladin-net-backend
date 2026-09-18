using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;
using Palladin.Core.Api;
using Palladin.Core.Security;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure.Consents;
using Palladin.Module.Identity.Infrastructure.Persistence;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record UpdateUserConsentRequest
{
    public string Purpose { get; init; } = string.Empty;
    public bool? Granted { get; init; }
    public uint? ExpectedRevision { get; init; }
    public Guid RequestId { get; init; }
    public string NoticeVersion { get; init; } = string.Empty;
    public string Locale { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
}

[UsedImplicitly]
internal sealed class UpdateUserConsentValidator : Validator<UpdateUserConsentRequest>
{
    public UpdateUserConsentValidator()
    {
        RuleFor(request => request.Purpose).Must(purpose => purpose is ConsentPurpose.ProductAnalytics or ConsentPurpose.EmailMarketing);
        RuleFor(request => request.Granted).NotNull();
        RuleFor(request => request.ExpectedRevision).NotNull();
        RuleFor(request => request.RequestId).NotEmpty();
        RuleFor(request => request.NoticeVersion).NotEmpty().MaximumLength(100);
        RuleFor(request => request.Locale).Must(locale => locale is "pl" or "en");
        RuleFor(request => request.Source).Must(source => source is "web_onboarding" or "web_settings" or "mobile_onboarding" or "mobile_settings");
    }
}

[PublicAPI]
internal sealed class UpdateUserConsentEndpoint(IdentityDomainWriteContext context, ConsentNoticeCatalog notices, IClock clock)
    : Endpoint<UpdateUserConsentRequest, UserConsentResponse>
{
    public override void Configure()
    {
        Put("api/account/consents/{Purpose}");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Options(builder => builder.AllowNonActiveOrganizationMembership());
        Tags("Identity/Account");
        Summary(summary =>
        {
            summary.Summary = "Record an optional consent decision for the authenticated account";
            summary.Description = "Account preferences remain writable during organization removal. Requires an observed revision and idempotency ID; stale writes return 409. A replay returns the current state, including any later withdrawal.";
        });
    }

    public override async Task HandleAsync(UpdateUserConsentRequest req, CancellationToken ct)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        var userId = User.GetUserId();
        if (userId is null || !await context.Users.AnyAsync(user => user.Id == userId.Value, ct))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        if (await TryReplayAsync(userId.Value, req, ct))
        {
            return;
        }

        var consent = await context.UserConsents.SingleOrDefaultAsync(
            value => value.UserId == userId.Value && value.Purpose == req.Purpose, ct);
        if ((consent?.Revision ?? 0) != req.ExpectedRevision)
        {
            await ConflictAsync(ct);
            return;
        }

        var notice = notices.Find(req.Purpose, req.Locale, req.NoticeVersion, clock.GetCurrentInstant());
        if (notice is null)
        {
            if (req.Granted == false && consent is not null)
            {
                notice = await context.UserConsentHistory
                    .Where(value => value.UserId == userId.Value && value.Purpose == req.Purpose
                        && value.Revision == consent.Revision && value.NoticeVersion == req.NoticeVersion && value.Locale == req.Locale)
                    .Select(value => new ConsentNotice(value.Purpose, value.Scope, value.NoticeVersion, value.Locale))
                    .SingleOrDefaultAsync(ct);
            }
        }

        if (notice is null)
        {
            AddError(ErrorResponses.General("consent-notice-unavailable"));
            await Send.ErrorsAsync(StatusCodes.Status409Conflict, ct);
            return;
        }

        if (consent is null)
        {
            consent = UserConsent.Create(userId.Value, req.Purpose);
            context.Add(consent);
        }

        var history = consent.TryDecide(req.Granted!.Value, req.ExpectedRevision!.Value,
            req.RequestId, notice, req.Source, clock.GetCurrentInstant());
        if (history is null)
        {
            await ConflictAsync(ct);
            return;
        }

        context.Add(history);
        try
        {
            await context.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            context.Clear();
            if (!await TryReplayAsync(userId.Value, req, ct))
            {
                await ConflictAsync(ct);
            }
            return;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "PK_UserConsents" or "PK_UserConsentHistory" or "IX_UserConsentHistory_UserId_Purpose_RequestId" })
        {
            context.Clear();
            if (!await TryReplayAsync(userId.Value, req, ct))
            {
                await ConflictAsync(ct);
            }
            return;
        }

        await Send.OkAsync(UserConsentResponses.Create(req.Purpose, consent), ct);
    }

    private async Task<bool> TryReplayAsync(Guid userId, UpdateUserConsentRequest req, CancellationToken ct)
    {
        var history = await context.UserConsentHistory.AsNoTracking().SingleOrDefaultAsync(
            value => value.UserId == userId && value.Purpose == req.Purpose && value.RequestId == req.RequestId, ct);
        if (history is null)
        {
            return false;
        }

        if (!history.Matches(req.Granted!.Value, req.ExpectedRevision!.Value, req.NoticeVersion, req.Locale, req.Source))
        {
            await ConflictAsync(ct);
            return true;
        }

        var current = await context.UserConsents.AsNoTracking().SingleAsync(
            value => value.UserId == userId && value.Purpose == req.Purpose, ct);
        await Send.OkAsync(UserConsentResponses.Create(req.Purpose, current), ct);
        return true;
    }

    private Task ConflictAsync(CancellationToken ct)
    {
        AddError(ErrorResponses.General("consent-conflict"));
        return Send.ErrorsAsync(StatusCodes.Status409Conflict, ct);
    }
}
