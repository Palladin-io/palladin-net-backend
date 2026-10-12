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

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record RecordSharedUnlockActivityRequest
{
    public string RefreshToken { get; init; } = string.Empty;
    public Guid AuthorizationId { get; init; }
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))]
    public byte[] SourceGeneration { get; init; } = [];
    public long IdleDeadlineMs { get; init; }
}

[UsedImplicitly]
internal sealed class RecordSharedUnlockActivityValidator : Validator<RecordSharedUnlockActivityRequest>
{
    public RecordSharedUnlockActivityValidator()
    {
        RuleFor(request => request.RefreshToken).NotEmpty().MaximumLength(1024);
        RuleFor(request => request.AuthorizationId).NotEmpty();
        RuleFor(request => request.SourceGeneration).Must(value => value is { Length: 32 });
        RuleFor(request => request.IdleDeadlineMs).InclusiveBetween(1, Instant.MaxValue.ToUnixTimeMilliseconds());
    }
}

[PublicAPI]
internal sealed class RecordSharedUnlockActivityEndpoint(RecordSharedUnlockActivityOperation operation) : IdentityOperationEndpoint<RecordSharedUnlockActivityRequest, AuthorizeSharedUnlockResponse>
{
    public override void Configure()
    {
        Post("api/account/shared-unlock/authorizations/activity");
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

    public override async Task HandleAsync(RecordSharedUnlockActivityRequest req, CancellationToken ct)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        var result = await operation.ExecuteAsync(req, HttpContext, ct);
        await SendResultAsync(result, ct);
    }
}
