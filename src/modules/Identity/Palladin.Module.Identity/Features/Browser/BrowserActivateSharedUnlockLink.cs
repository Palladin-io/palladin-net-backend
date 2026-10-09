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
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure.Jwt;
using Palladin.Module.Identity.Infrastructure.Persistence;

using Palladin.Module.Identity.Infrastructure.BrowserSessions;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record BrowserActivateSharedUnlockLinkRequest
{
    public Guid ExpectedSessionId { get; init; }

    public Guid LinkId { get; init; }
    public Guid AuthorizationId { get; init; }
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))]
    public byte[] SourceGeneration { get; init; } = [];
    public uint ExpectedRevision { get; init; }
    public uint ExpectedPreferenceRevision { get; init; }
    internal ActivateSharedUnlockLinkRequest WithCookie(string token) => new()
    {
        RefreshToken = token,
        LinkId = LinkId,
        AuthorizationId = AuthorizationId,
        SourceGeneration = SourceGeneration,
        ExpectedRevision = ExpectedRevision,
        ExpectedPreferenceRevision = ExpectedPreferenceRevision,
    };
}

[PublicAPI]
internal sealed class BrowserActivateSharedUnlockLinkEndpoint(ActivateSharedUnlockLinkOperation operation, IdentityDomainWriteContext context)
    : IdentityOperationEndpoint<BrowserActivateSharedUnlockLinkRequest, SharedUnlockLinkResponse>
{
    public override void Configure()
    {
        Post("api/browser/account/shared-unlock/links/{LinkId}/activate");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Tags("Identity/Account");
        Summary(summary => summary.Summary = "Bind current manual-unlock authority to a verified local browser link");
    }

    public override async Task HandleAsync(BrowserActivateSharedUnlockLinkRequest req, CancellationToken ct)
    {
        var raw = await BrowserOwnSession.ResolveAsync(HttpContext, req.ExpectedSessionId, context.RefreshTokens, ct);
        if (raw is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }
        var core = req.WithCookie(raw);
        var validation = await new ActivateSharedUnlockLinkValidator().ValidateAsync(core, ct);
        if (!validation.IsValid)
        {
            ValidationFailures.AddRange(validation.Errors);
            await Send.ErrorsAsync(400, ct);
            return;
        }
        await SendResultAsync(await operation.ExecuteAsync(core, HttpContext, ct), ct);
    }
}
