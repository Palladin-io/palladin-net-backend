using Palladin.Module.Identity.Contracts.ValueObjects;
using Palladin.Module.Identity.Domain.Enums;
using Palladin.Module.Identity.Infrastructure;
using Palladin.Module.Identity.Infrastructure.Jwt;
using Palladin.Module.Identity.Infrastructure.Login;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Infrastructure.Totp;
using Palladin.Module.Identity.Infrastructure.Waitlist;
using Palladin.Module.Identity.Shared;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Module.Identity.Domain;
using System.Globalization;

using Palladin.Module.Identity.Infrastructure.BrowserSessions;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
internal sealed class LoginTotpBrowserSessionEndpoint(LoginTotpOperation operation, BrowserSessionResponseWriter writer)
    : IdentityOperationEndpoint<LoginTotpRequest, object>
{
    public override void Configure()
    {
        Post("api/browser/auth/login/totp");
        // Anonymous by design: completes a login that already passed the password step, keyed by the
        // single-use challenge token. Accepts a TOTP code or a recovery code; attempts are rate-limited.
        AllowAnonymous();
        Summary(summary =>
        {
            summary.Summary = "Complete login with a TOTP or recovery code";
            summary.Description = "Redeems the short-lived login challenge with a 6-digit TOTP code (±1 window, "
                + "replay-protected) or a single-use recovery code, then issues the session.";
        });
        Validator<LoginTotpValidator>();
        Tags("Identity/Auth");
    }

    public override async Task HandleAsync(LoginTotpRequest req, CancellationToken ct)
    {
        var result = await operation.ExecuteAsync(req, HttpContext, ct);
        await SendResultAsync(await writer.MapAsync(result, HttpContext, ct), ct);
    }
}
