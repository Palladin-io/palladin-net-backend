using Palladin.Module.Identity.Shared;
using System.Text.Json.Serialization;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Api;
using Palladin.Core.Json;
using Palladin.Core.Security;
using Palladin.Module.Identity.Infrastructure.Jwt;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Infrastructure.SharedUnlock;

using Palladin.Module.Identity.Infrastructure.BrowserSessions;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record BrowserRecordSharedUnlockActivityRequest
{
    public Guid ExpectedSessionId { get; init; }

    public Guid AuthorizationId { get; init; }
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))]
    public byte[] SourceGeneration { get; init; } = [];
    public long IdleDeadlineMs { get; init; }
    internal RecordSharedUnlockActivityRequest WithCookie(string token) => new()
    {
        RefreshToken = token,
        AuthorizationId = AuthorizationId,
        SourceGeneration = SourceGeneration,
        IdleDeadlineMs = IdleDeadlineMs,
    };
}

[PublicAPI]
internal sealed class BrowserRecordSharedUnlockActivityEndpoint(RecordSharedUnlockActivityOperation operation, IdentityDomainWriteContext context)
    : IdentityOperationEndpoint<BrowserRecordSharedUnlockActivityRequest, AuthorizeSharedUnlockResponse>
{
    public override void Configure()
    {
        Post("api/browser/account/shared-unlock/authorizations/activity");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Tags("Identity/Account");
        Summary(summary =>
        {
            summary.Summary = "Record a still-unlocked client's actual activity without renewing its hard limits";
            summary.Description = "The trusted client supplies the absolute idle deadline calculated at the real activity event. "
                + "Replays cannot add server time. Expired or invalidated authority cannot be revived. "
                + "This updates only the source's own authority, including while sharing is disabled.";
        });
    }

    public override async Task HandleAsync(BrowserRecordSharedUnlockActivityRequest req, CancellationToken ct)
    {
        var raw = await BrowserOwnSession.ResolveAsync(HttpContext, req.ExpectedSessionId, context.RefreshTokens, ct);
        if (raw is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }
        var core = req.WithCookie(raw);
        var validation = await new RecordSharedUnlockActivityValidator().ValidateAsync(core, ct);
        if (!validation.IsValid)
        {
            ValidationFailures.AddRange(validation.Errors);
            await Send.ErrorsAsync(400, ct);
            return;
        }
        await SendResultAsync(await operation.ExecuteAsync(core, HttpContext, ct), ct);
    }
}
