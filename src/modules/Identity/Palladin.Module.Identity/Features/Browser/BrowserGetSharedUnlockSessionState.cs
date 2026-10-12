using Palladin.Module.Identity.Shared;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Security;
using Palladin.Module.Identity.Infrastructure;
using Palladin.Module.Identity.Infrastructure.Jwt;
using Palladin.Module.Identity.Infrastructure.Persistence;

using Palladin.Module.Identity.Infrastructure.BrowserSessions;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record BrowserGetSharedUnlockSessionStateRequest
{
    public Guid ExpectedSessionId { get; init; }

    public Guid LinkId { get; init; }
    internal GetSharedUnlockSessionStateRequest WithCookie(string token) => new()
    {
        RefreshToken = token,
        LinkId = LinkId,
    };
}

[PublicAPI]
internal sealed class BrowserGetSharedUnlockSessionStateEndpoint(GetSharedUnlockSessionStateOperation operation, IdentityDomainReadContext context)
    : IdentityOperationEndpoint<BrowserGetSharedUnlockSessionStateRequest, SharedUnlockSessionStateResponse>
{
    public override void Configure()
    {
        Post("api/browser/account/shared-unlock/session-state");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Options(builder => builder.AllowNonActiveOrganizationMembership());
        Tags("Identity/Account");
        Summary(summary =>
        {
            summary.Summary = "Read closing actions for this client's own logical session";
            summary.Description = "Read-only POST keeps the own refresh token out of URLs. No action is unlock authority. "
                + "The response neither renews a session nor returns its source authorization or generation.";
        });
    }

    public override async Task HandleAsync(BrowserGetSharedUnlockSessionStateRequest req, CancellationToken ct)
    {
        var raw = await BrowserOwnSession.ResolveAsync(HttpContext, req.ExpectedSessionId, context.RefreshTokens, ct);
        if (raw is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }
        var core = req.WithCookie(raw);
        var validation = await new GetSharedUnlockSessionStateValidator().ValidateAsync(core, ct);
        if (!validation.IsValid)
        {
            ValidationFailures.AddRange(validation.Errors);
            await Send.ErrorsAsync(400, ct);
            return;
        }
        await SendResultAsync(await operation.ExecuteAsync(core, HttpContext, ct), ct);
    }
}
