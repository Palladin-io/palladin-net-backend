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

using Microsoft.AspNetCore.Http;
using Palladin.Module.Identity.Shared;

namespace Palladin.Module.Identity.Features;

internal sealed class GetSharedUnlockSessionStateOperation(IdentityDomainReadContext context, IClock clock)
{
    public async Task<IdentityOperationResult<SharedUnlockSessionStateResponse>> ExecuteAsync(GetSharedUnlockSessionStateRequest req, HttpContext httpContext, CancellationToken ct)
    {
        var userId = httpContext.User.GetUserId();
        var organizationId = httpContext.User.GetOrganizationId();
        var hash = TokenService.HashToken(req.RefreshToken);
        var own = await context.RefreshTokens
            .Where(token => token.UserId == userId && token.OrganizationId == organizationId && token.TokenHash == hash)
            .Select(token => new
            {
                token.ExpiresAt,
                token.RevokedAt,
                token.AuthorizationVersion,
                Root = context.SharedUnlockAuthorizations
                    .Where(root => root.UserId == userId && root.SessionId == (token.SessionId ?? token.Id))
                    .Select(root => new { root.Sequence, root.LinkId }).SingleOrDefault(),
                Link = context.SharedUnlockLinks.Where(link => link.UserId == userId && link.Id == req.LinkId)
                    .Select(link => new { link.Id, link.Revision, link.Epoch, link.State,
                        link.LastInvalidationSequence, link.LastLogoutSequence }).SingleOrDefault(),
            }).SingleOrDefaultAsync(ct);
        if (own is null || own.RevokedAt is not null || clock.GetCurrentInstant() >= own.ExpiresAt
            || own.AuthorizationVersion != httpContext.User.GetAuthorizationVersion())
        {
            return IdentityOperationResult<SharedUnlockSessionStateResponse>.Status(401);
        }

        var action = own.Root?.LinkId != req.LinkId ? "none"
            : own.Link is null || own.Root.Sequence <= own.Link.LastLogoutSequence ? "logout"
            : own.Root.Sequence <= own.Link.LastInvalidationSequence ? "lock" : "none";
        var responseLink = own.Link is { } link
            ? new SharedUnlockLinkResponse(link.Id, link.Revision, link.Epoch, link.State.ToString().ToLowerInvariant(),
                link.LastInvalidationSequence, link.LastLogoutSequence)
            : null;
        return IdentityOperationResult<SharedUnlockSessionStateResponse>.Ok(new SharedUnlockSessionStateResponse(action, responseLink));
    }
}
