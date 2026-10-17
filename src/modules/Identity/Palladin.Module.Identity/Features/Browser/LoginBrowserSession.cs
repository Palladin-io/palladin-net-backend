using Palladin.Module.Identity.Shared;
using Palladin.Core.Guid;
using Palladin.Module.Identity.Contracts.ValueObjects;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure;
using Palladin.Module.Identity.Infrastructure.Jwt;
using Palladin.Module.Identity.Infrastructure.Login;
using Palladin.Module.Identity.Infrastructure.PasswordAuth;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Infrastructure.Waitlist;
using Palladin.Module.Identity.Infrastructure.Totp;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using System.Text.Json.Serialization;
using Palladin.Core.Json;
using System.Globalization;

using Palladin.Module.Identity.Infrastructure.BrowserSessions;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
internal sealed class LoginBrowserSessionEndpoint(LoginOperation operation, BrowserSessionResponseWriter writer)
    : IdentityOperationEndpoint<LoginRequest, object>
{
    public override void Configure()
    {
        Post("api/browser/auth/login");
        // Anonymous by design: this IS the authentication. Responses are generic to avoid account
        // enumeration; failures are rate-limited per IP/account and locked out per account.
        AllowAnonymous();
        Summary(summary =>
        {
            summary.Summary = "Log in with email + password";
            summary.Description = "Verifies the client authHash (constant-time). Returns a session, or a "
                + "short-lived TOTP challenge when the second factor is enabled. Bad credentials return a "
                + "generic 401; repeated failures for the account are locked out with 429.";
        });
        Validator<LoginValidator>();
        Tags("Identity/Auth");
    }

    public override async Task HandleAsync(LoginRequest req, CancellationToken ct)
    {
        var result = await operation.ExecuteAsync(req, HttpContext, ct);
        await SendResultAsync(await writer.MapAsync(result, HttpContext, ct), ct);
    }
}
