using Palladin.Module.Identity.Shared;
using System.Globalization;
using System.Text.Json.Serialization;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;
using Palladin.Core.Api;
using Palladin.Core.Guid;
using Palladin.Core.Json;
using Palladin.Core.Security;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Contracts.ValueObjects;
using Palladin.Module.Identity.Infrastructure.Jwt;
using Palladin.Module.Identity.Infrastructure.Login;
using Palladin.Module.Identity.Infrastructure.PasswordAuth;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Infrastructure.SharedUnlock;

using Palladin.Module.Identity.Infrastructure.BrowserSessions;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record BrowserAuthorizeSharedUnlockRequest
{
    public Guid ExpectedSessionId { get; init; }

    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))]
    public byte[] AuthCredential { get; init; } = [];
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))]
    public byte[] SourceGeneration { get; init; } = [];
    public uint ExpectedPreferenceRevision { get; init; }
    public uint ExpectedCredentialRevision { get; init; }
    public uint ExpectedPrivateKeyWrapRevision { get; init; }
    public long IdleDeadlineMs { get; init; }
    public long AbsoluteDeadlineMs { get; init; }
    public long OfflineDeadlineMs { get; init; }
    internal AuthorizeSharedUnlockRequest WithCookie(string token) => new()
    {
        RefreshToken = token,
        AuthCredential = AuthCredential,
        SourceGeneration = SourceGeneration,
        ExpectedPreferenceRevision = ExpectedPreferenceRevision,
        ExpectedCredentialRevision = ExpectedCredentialRevision,
        ExpectedPrivateKeyWrapRevision = ExpectedPrivateKeyWrapRevision,
        IdleDeadlineMs = IdleDeadlineMs,
        AbsoluteDeadlineMs = AbsoluteDeadlineMs,
        OfflineDeadlineMs = OfflineDeadlineMs,
    };
}

[PublicAPI]
internal sealed class BrowserAuthorizeSharedUnlockEndpoint(AuthorizeSharedUnlockOperation operation, IdentityDomainWriteContext context)
    : IdentityOperationEndpoint<BrowserAuthorizeSharedUnlockRequest, AuthorizeSharedUnlockResponse>
{
    public override void Configure()
    {
        Post("api/browser/account/shared-unlock/authorizations");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Tags("Identity/Account");
        Summary(summary => summary.Summary = "Authorize a fresh manual unlock using the source's own session and password proof");
    }

    public override async Task HandleAsync(BrowserAuthorizeSharedUnlockRequest req, CancellationToken ct)
    {
        var raw = await BrowserOwnSession.ResolveAsync(HttpContext, req.ExpectedSessionId, context.RefreshTokens, ct);
        if (raw is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }
        var core = req.WithCookie(raw);
        var validation = await new AuthorizeSharedUnlockValidator().ValidateAsync(core, ct);
        if (!validation.IsValid)
        {
            ValidationFailures.AddRange(validation.Errors);
            await Send.ErrorsAsync(400, ct);
            return;
        }
        await SendResultAsync(await operation.ExecuteAsync(core, HttpContext, ct), ct);
    }
}
