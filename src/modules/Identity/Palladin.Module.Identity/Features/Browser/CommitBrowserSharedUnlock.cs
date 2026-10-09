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
public sealed record BrowserSharedUnlockCommitResponse(BrowserAuthSessionResponse Session,
    Guid AuthorizationId, uint AuthorizationSequence, SharedUnlockTransferContext Context);

[PublicAPI]
internal sealed class CommitBrowserSharedUnlockEndpoint(CommitSharedUnlockOperationHandler operation,
    BrowserSessionResponseWriter writer, IdentityDomainWriteContext context)
    : IdentityOperationEndpoint<CommitSharedUnlockOperationRequest, object>
{
    public override void Configure()
    {
        Post("api/browser/auth/shared-unlock/operations/{OperationId}/commit");
        // The receiver's Ed25519 proof authenticates this one operation; the origin guard protects cookie mutation.
        AllowAnonymous();
        Validator<CommitSharedUnlockOperationValidator>();
        Tags("Identity/Browser");
    }

    public override async Task HandleAsync(CommitSharedUnlockOperationRequest req, CancellationToken ct)
    {
        var offered = await context.SharedUnlockOperations.SingleOrDefaultAsync(item => item.Id == req.OperationId, ct);
        if (offered is null || offered.Direction != SharedUnlockDirection.ExtensionToWeb
            || offered.WebOrigin != HttpContext.Request.Headers.Origin)
        {
            await Send.ForbiddenAsync(ct);
            return;
        }
        var raw = BrowserSessionCookie.Read(HttpContext);
        if (raw is not null)
        {
            var hash = TokenService.HashToken(raw);
            var previous = await context.RefreshTokens.SingleOrDefaultAsync(token => token.TokenHash == hash, ct);
            if (previous is not null && (previous.UserId != offered.UserId || previous.OrganizationId != offered.OrganizationId))
            {
                await Send.StatusCodeAsync(409, ct);
                return;
            }
        }
        var result = await operation.ExecuteAsync(req, HttpContext, ct);
        await SendResultAsync(await writer.MapAsync(result, HttpContext, ct), ct);
    }
}
