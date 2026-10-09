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

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record AuthorizeSharedUnlockRequest
{
    public string RefreshToken { get; init; } = string.Empty;
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
}

[PublicAPI]
public sealed record AuthorizeSharedUnlockResponse(Guid AuthorizationId, uint Sequence,
    Guid AccountId, Guid OrganizationId, uint CredentialRevision, uint PrivateKeyWrapRevision,
    uint AuthorizationVersion, long UnlockedAtMs, long IdleDeadlineMs,
    long AbsoluteDeadlineMs, long OfflineDeadlineMs);

[UsedImplicitly]
internal sealed class AuthorizeSharedUnlockValidator : Validator<AuthorizeSharedUnlockRequest>
{
    public AuthorizeSharedUnlockValidator()
    {
        RuleFor(request => request.RefreshToken).NotEmpty().MaximumLength(1024);
        RuleFor(request => request.AuthCredential).Must(value => value is { Length: IdentityKdfProfiles.AuthCredentialBytes });
        RuleFor(request => request.SourceGeneration).Must(value => value is { Length: 32 });
        RuleFor(request => request.ExpectedPreferenceRevision).GreaterThan(0u);
        RuleFor(request => request.ExpectedCredentialRevision).GreaterThan(0u);
        RuleFor(request => request.ExpectedPrivateKeyWrapRevision).GreaterThan(0u);
        RuleFor(request => request.IdleDeadlineMs).InclusiveBetween(1, Instant.MaxValue.ToUnixTimeMilliseconds());
        RuleFor(request => request.AbsoluteDeadlineMs).InclusiveBetween(1, Instant.MaxValue.ToUnixTimeMilliseconds());
        RuleFor(request => request.OfflineDeadlineMs).InclusiveBetween(1, Instant.MaxValue.ToUnixTimeMilliseconds());
    }
}

[PublicAPI]
internal sealed class AuthorizeSharedUnlockEndpoint(AuthorizeSharedUnlockOperation operation) : IdentityOperationEndpoint<AuthorizeSharedUnlockRequest, AuthorizeSharedUnlockResponse>
{
    public override void Configure()
    {
        Post("api/account/shared-unlock/authorizations");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Tags("Identity/Account");
        Summary(summary => summary.Summary = "Authorize a fresh manual unlock using the source's own session and password proof");
    }

    public override async Task HandleAsync(AuthorizeSharedUnlockRequest req, CancellationToken ct)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        var result = await operation.ExecuteAsync(req, HttpContext, ct);
        await SendResultAsync(result, ct);
    }
}
