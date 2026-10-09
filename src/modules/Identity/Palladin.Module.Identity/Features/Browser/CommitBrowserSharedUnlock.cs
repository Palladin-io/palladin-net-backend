using System.Text.Json.Serialization;
using Palladin.Core.Json;
using NodaTime;
using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure.BrowserSessions;
using Palladin.Module.Identity.Infrastructure.Jwt;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Infrastructure.SharedUnlock;
using Palladin.Module.Identity.Shared;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record BrowserSharedUnlockCommitRequest
{
    public Guid OperationId { get; init; }
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] public byte[] Signature { get; init; } = [];
    public Guid? ExpectedSessionId { get; init; }
}

[PublicAPI]
public sealed record BrowserSharedUnlockCommitResponse(BrowserAuthSessionResponse Session,
    Guid AuthorizationId, uint AuthorizationSequence, SharedUnlockTransferContext Context);

[PublicAPI]
internal sealed class CommitBrowserSharedUnlockEndpoint(CommitSharedUnlockOperationHandler operation,
    BrowserSessionResponseWriter writer, IdentityDomainWriteContext context, IClock clock)
    : IdentityOperationEndpoint<BrowserSharedUnlockCommitRequest, object>
{
    public override void Configure()
    {
        Post("api/browser/auth/shared-unlock/operations/{OperationId}/commit");
        // The receiver's Ed25519 proof authenticates this one operation; the origin guard protects cookie mutation.
        AllowAnonymous();
        Tags("Identity/Browser");
    }

    public override async Task HandleAsync(BrowserSharedUnlockCommitRequest req, CancellationToken ct)
    {
        var offered = await context.SharedUnlockOperations.SingleOrDefaultAsync(item => item.Id == req.OperationId, ct);
        if (offered is null || offered.Direction != SharedUnlockDirection.ExtensionToWeb
            || offered.WebOrigin != HttpContext.Request.Headers.Origin)
        {
            await Send.ForbiddenAsync(ct);
            return;
        }
        var mapped = new CommitSharedUnlockOperationRequest { OperationId = req.OperationId, Signature = req.Signature };
        var validation = await new CommitSharedUnlockOperationValidator().ValidateAsync(mapped, ct);
        if (!validation.IsValid)
        {
            foreach (var error in validation.Errors) AddError(error.ErrorMessage);
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }
        var raw = BrowserSessionCookie.Read(HttpContext);
        var hash = raw is null ? null : TokenService.HashToken(raw);
        var previous = hash is null ? null : await context.RefreshTokens.SingleOrDefaultAsync(token => token.TokenHash == hash, ct);
        var live = previous is not null && previous.RevokedAt is null && previous.ExpiresAt > clock.GetCurrentInstant();
        if (req.ExpectedSessionId is { } expected
            ? !live || (previous!.SessionId ?? previous.Id) != expected
            : live)
        {
            await Send.StatusCodeAsync(409, ct);
            return;
        }
        if (live && (previous!.UserId != offered.UserId || previous.OrganizationId != offered.OrganizationId))
        {
            await Send.StatusCodeAsync(409, ct);
            return;
        }
        var result = await operation.ExecuteAsync(mapped, HttpContext, ct);
        await SendResultAsync(await writer.MapAsync(result, HttpContext, ct), ct);
    }
}
