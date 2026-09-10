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

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record CreateSharedUnlockOperationRequest
{
    public string RefreshToken { get; init; } = string.Empty;
    public Guid AuthorizationId { get; init; }
    public Guid LinkId { get; init; }
    public uint LinkEpoch { get; init; }
    public uint ExpectedPreferenceRevision { get; init; }
    public Guid RecipientOrganizationId { get; init; }
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
}

[PublicAPI]
public sealed record SharedUnlockTransferContext(string Protocol, string Direction,
    Guid OperationId, Guid AccountId, Guid OrganizationId, string ApiOrigin, string WebOrigin,
    string ExtensionId, string DocumentBinding, string WebGeneration, string ExtensionGeneration,
    Guid LinkId, uint LinkEpoch, uint PreferenceRevision, uint AuthorizationVersion,
    string KeyContextDigest, long IssuedAtMs, long ExpiresAtMs, long UnlockedAtMs,
    long IdleDeadlineMs, long AbsoluteDeadlineMs, long OfflineDeadlineMs);

[PublicAPI]
public sealed record SharedUnlockOperationResponse(SharedUnlockTransferContext Context,
    string SourcePublicKey, string RecipientPublicKey, string RecipientProofPublicKey,
    string Challenge, string TranscriptHash, SharedUnlockKeyContext KeyContext)
{
    internal static SharedUnlockOperationResponse From(SharedUnlockOperation operation, SharedUnlockKeyContext keyContext) =>
        new(new SharedUnlockTransferContext(SharedUnlockTranscript.Protocol,
            SharedUnlockTranscript.Direction(operation.Direction), operation.Id, operation.UserId,
            operation.OrganizationId, operation.ApiOrigin, operation.WebOrigin, operation.ExtensionId,
            operation.DocumentBinding, Base64Url.EncodeToString(operation.WebGeneration),
            Base64Url.EncodeToString(operation.ExtensionGeneration), operation.LinkId, operation.LinkEpoch,
            operation.PreferenceRevision, operation.AuthorizationVersion,
            Base64Url.EncodeToString(operation.KeyContextDigest), operation.IssuedAt.ToUnixTimeMilliseconds(),
            operation.ExpiresAt.ToUnixTimeMilliseconds(), operation.UnlockedAt.ToUnixTimeMilliseconds(),
            operation.IdleDeadline.ToUnixTimeMilliseconds(), operation.AbsoluteDeadline.ToUnixTimeMilliseconds(),
            operation.OfflineDeadline.ToUnixTimeMilliseconds()), Base64Url.EncodeToString(operation.SourcePublicKey),
            Base64Url.EncodeToString(operation.RecipientPublicKey), Base64Url.EncodeToString(operation.RecipientProofPublicKey),
            Base64Url.EncodeToString(operation.Challenge), Base64Url.EncodeToString(operation.TranscriptHash), keyContext);
}

[UsedImplicitly]
internal sealed class CreateSharedUnlockOperationValidator : Validator<CreateSharedUnlockOperationRequest>
{
    public CreateSharedUnlockOperationValidator()
    {
        RuleFor(request => request.RefreshToken).NotEmpty().MaximumLength(1024);
        RuleFor(request => request.AuthorizationId).NotEmpty();
        RuleFor(request => request.LinkId).NotEmpty();
        RuleFor(request => request.LinkEpoch).GreaterThan(0u);
        RuleFor(request => request.ExpectedPreferenceRevision).GreaterThan(0u);
        RuleFor(request => request.RecipientOrganizationId).NotEmpty();
        RuleFor(request => request.Direction).Must(value => value is "web-to-extension" or "extension-to-web");
        RuleFor(request => request.ApiOrigin).MaximumLength(256).Must(IsOrigin);
        RuleFor(request => request.WebOrigin).MaximumLength(256).Must(IsOrigin);
        RuleFor(request => request.ExtensionId).NotEmpty().MaximumLength(256).Matches("^[!-~]+$");
        RuleFor(request => request.DocumentBinding).NotEmpty().MaximumLength(256).Matches("^[!-~]+$");
        RuleFor(request => request.WebGeneration).Must(value => value is { Length: 32 });
        RuleFor(request => request.ExtensionGeneration).Must(value => value is { Length: 32 });
        RuleFor(request => request.SourcePublicKey).Must(value => value is { Length: 32 });
        RuleFor(request => request.RecipientPublicKey).Must(value => value is { Length: 32 });
        RuleFor(request => request.RecipientProofPublicKey).Must(value => value is { Length: 32 });
    }

    private static bool IsOrigin(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.Host is "localhost" or "127.0.0.1" or "[::1]"))
        && uri.UserInfo.Length == 0 && uri.GetLeftPart(UriPartial.Authority) == value;
}

[PublicAPI]
internal sealed class CreateSharedUnlockOperationEndpoint(IdentityDomainWriteContext context,
    IGuidProvider guidProvider, IClock clock, IOptionsMonitor<SharedUnlockOptions> options)
    : Endpoint<CreateSharedUnlockOperationRequest, SharedUnlockOperationResponse>
{
    public override void Configure()
    {
        Post("api/account/shared-unlock/operations");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Tags("Identity/Account");
        Summary(summary => summary.Summary = "Authorize one receiver-bound shared-unlock operation");
    }

    public override async Task HandleAsync(CreateSharedUnlockOperationRequest req, CancellationToken ct)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        if (!options.CurrentValue.Enabled)
        {
            await Send.StatusCodeAsync(StatusCodes.Status503ServiceUnavailable, ct);
            return;
        }

        var userId = User.GetUserId();
        var organizationId = User.GetOrganizationId();
        var hash = TokenService.HashToken(req.RefreshToken);
        var session = await context.RefreshTokens.SingleOrDefaultAsync(token =>
            token.UserId == userId && token.OrganizationId == organizationId && token.TokenHash == hash, ct);
        if (session is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }
        var direction = req.Direction == "web-to-extension"
            ? SharedUnlockDirection.WebToExtension : SharedUnlockDirection.ExtensionToWeb;
        var now = clock.GetCurrentInstant();
        var authority = await SharedUnlockOperationAuthority.LoadAsync(context, session, req.AuthorizationId,
            req.RecipientOrganizationId, req.LinkId, req.LinkEpoch, req.ExpectedPreferenceRevision,
            direction == SharedUnlockDirection.WebToExtension ? req.WebGeneration : req.ExtensionGeneration, now, ct);
        if (authority is null || authority.SourceMember.AuthorizationVersion != User.GetAuthorizationVersion())
        {
            await SendUnavailableAsync(ct);
            return;
        }
        var operation = SharedUnlockOperation.Create(guidProvider.Generate(), authority.User, authority.Source,
            session, authority.SourceOrganization, authority.TargetOrganization, authority.TargetMember, authority.Link,
            new SharedUnlockChannel(direction, req.ApiOrigin, req.WebOrigin, req.ExtensionId, req.DocumentBinding,
                req.WebGeneration, req.ExtensionGeneration, req.SourcePublicKey, req.RecipientPublicKey,
                req.RecipientProofPublicKey), SharedUnlockKeyContextDigest.Hash(authority.KeyContext),
            SharedUnlockTranscript.Challenge(), Instant.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds()));
        operation.BindTranscriptHash(SharedUnlockTranscript.Hash(operation));
        context.Add(operation);
        authority.Fence(context);
        if (clock.GetCurrentInstant() >= operation.ExpiresAt)
        {
            await SendUnavailableAsync(ct);
            return;
        }
        if (!options.CurrentValue.Enabled)
        {
            await Send.StatusCodeAsync(StatusCodes.Status503ServiceUnavailable, ct);
            return;
        }
        try
        {
            await context.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await SendUnavailableAsync(ct);
            return;
        }
        await Send.OkAsync(SharedUnlockOperationResponse.From(operation, authority.KeyContext), ct);
    }

    private async Task SendUnavailableAsync(CancellationToken ct)
    {
        AddError(ErrorResponses.General("shared-unlock-operation-unavailable"));
        await Send.ErrorsAsync(StatusCodes.Status409Conflict, ct);
    }
}
