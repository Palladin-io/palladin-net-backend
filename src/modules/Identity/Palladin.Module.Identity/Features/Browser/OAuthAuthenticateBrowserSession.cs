using Palladin.Module.Identity.Shared;
using Palladin.Core.Guid;
using Palladin.Core.Security;
using Palladin.Core.Transport;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Contracts.ValueObjects;
using Palladin.Module.Identity.Domain.Enums;
using Palladin.Module.Identity.Infrastructure.Jwt;
using Palladin.Module.Identity.Infrastructure.OAuth;
using Palladin.Module.Identity.Infrastructure.Options;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Infrastructure.Waitlist;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using Serilog;

using Palladin.Module.Identity.Infrastructure.BrowserSessions;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
internal sealed class OAuthAuthenticateBrowserSessionEndpoint(OAuthAuthenticateOperation operation, BrowserSessionResponseWriter writer)
    : IdentityOperationEndpoint<OAuthAuthenticateRequest, object>
{
    public override void Configure()
    {
        Post("api/browser/auth/oauth/{Provider}");
        AllowAnonymous();
        Summary(summary =>
        {
            summary.Summary = "Authenticate via OAuth provider";
            summary.Description = "Authenticates a user via an external OAuth provider (Google, Apple, X). Creates a new account if the user does not exist.";
        });
        Validator<OAuthAuthenticateValidator>();
        Tags("Identity/Auth");
    }

    public override async Task HandleAsync(OAuthAuthenticateRequest req, CancellationToken ct)
    {
        var result = await operation.ExecuteAsync(req, HttpContext, ct);
        await SendResultAsync(await writer.MapAsync(result, HttpContext, ct), ct);
    }
}
