using System.Buffers.Text;
using System.Text.Json.Serialization;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using Palladin.Core.Api;
using Palladin.Core.Guid;
using Palladin.Core.Json;
using Palladin.Core.Security;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure.Jwt;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Infrastructure.SharedUnlock;
using Palladin.Module.Identity.Shared;

using Palladin.Module.Identity.Infrastructure.BrowserSessions;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record BrowserCreateSharedUnlockOperationRequest
{
    public Guid ExpectedSessionId { get; init; }

    public Guid AuthorizationId { get; init; }
    public Guid LinkId { get; init; }
    public uint LinkEpoch { get; init; }
    public uint ExpectedPreferenceRevision { get; init; }
    public Guid RecipientOrganizationId { get; init; }
    public long IdleDeadlineMs { get; init; }
    public long AbsoluteDeadlineMs { get; init; }
    public long OfflineDeadlineMs { get; init; }
    public string Direction { get; init; } = string.Empty;
    public string ApiOrigin { get; init; } = string.Empty;
    public string WebOrigin { get; init; } = string.Empty;
    public string ExtensionId { get; init; } = string.Empty;
    public string DocumentBinding { get; init; } = string.Empty;
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] public byte[] WebGeneration { get; init; } = [];
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] public byte[] ExtensionGeneration { get; init; } = [];
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] public byte[] SourcePublicKey { get; init; } = [];
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] public byte[] RecipientPublicKey { get; init; } = [];
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] public byte[] RecipientProofPublicKey { get; init; } = [];
    internal CreateSharedUnlockOperationRequest WithCookie(string token) => new()
    {
        RefreshToken = token,
        AuthorizationId = AuthorizationId,
        LinkId = LinkId,
        LinkEpoch = LinkEpoch,
        ExpectedPreferenceRevision = ExpectedPreferenceRevision,
        RecipientOrganizationId = RecipientOrganizationId,
        IdleDeadlineMs = IdleDeadlineMs,
        AbsoluteDeadlineMs = AbsoluteDeadlineMs,
        OfflineDeadlineMs = OfflineDeadlineMs,
        Direction = Direction,
        ApiOrigin = ApiOrigin,
        WebOrigin = WebOrigin,
        ExtensionId = ExtensionId,
        DocumentBinding = DocumentBinding,
        WebGeneration = WebGeneration,
        ExtensionGeneration = ExtensionGeneration,
        SourcePublicKey = SourcePublicKey,
        RecipientPublicKey = RecipientPublicKey,
        RecipientProofPublicKey = RecipientProofPublicKey,
    };
}

[PublicAPI]
internal sealed class BrowserCreateSharedUnlockOperationEndpoint(CreateSharedUnlockOperationHandler operation, IdentityDomainWriteContext context)
    : IdentityOperationEndpoint<BrowserCreateSharedUnlockOperationRequest, SharedUnlockOperationResponse>
{
    public override void Configure()
    {
        Post("api/browser/account/shared-unlock/operations");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Tags("Identity/Account");
        Summary(summary => summary.Summary = "Authorize one receiver-bound shared-unlock operation");
    }

    public override async Task HandleAsync(BrowserCreateSharedUnlockOperationRequest req, CancellationToken ct)
    {
        var raw = await BrowserOwnSession.ResolveAsync(HttpContext, req.ExpectedSessionId, context.RefreshTokens, ct);
        if (raw is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }
        var core = req.WithCookie(raw);
        var validation = await new CreateSharedUnlockOperationValidator().ValidateAsync(core, ct);
        if (!validation.IsValid)
        {
            ValidationFailures.AddRange(validation.Errors);
            await Send.ErrorsAsync(400, ct);
            return;
        }
        if (req.Direction != "web-to-extension" || req.WebOrigin != HttpContext.Request.Headers.Origin)
        {
            await Send.ForbiddenAsync(ct);
            return;
        }
        await SendResultAsync(await operation.ExecuteAsync(core, HttpContext, ct), ct);
    }
}
