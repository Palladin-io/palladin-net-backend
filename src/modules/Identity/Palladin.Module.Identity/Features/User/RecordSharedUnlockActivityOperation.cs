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

using Palladin.Module.Identity.Shared;

namespace Palladin.Module.Identity.Features;

internal sealed class RecordSharedUnlockActivityOperation(IdentityDomainWriteContext context, IClock clock)
{
    public async Task<IdentityOperationResult<AuthorizeSharedUnlockResponse>> ExecuteAsync(RecordSharedUnlockActivityRequest req, HttpContext httpContext, CancellationToken ct)
    {
        var userId = httpContext.User.GetUserId();
        var organizationId = httpContext.User.GetOrganizationId();
        var hash = TokenService.HashToken(req.RefreshToken);
        var session = await context.RefreshTokens.SingleOrDefaultAsync(token => token.UserId == userId
            && token.OrganizationId == organizationId && token.TokenHash == hash, ct);
        var now = clock.GetCurrentInstant();
        if (session is null || !session.IsActive(now))
        {
            return IdentityOperationResult<AuthorizeSharedUnlockResponse>.Status(401);
        }
        var revocation = await SharedUnlockSessionRevocation.LoadAsync(context, session, ct);
        if (revocation.IsRevoked)
        {
            return IdentityOperationResult<AuthorizeSharedUnlockResponse>.Status(401);
        }
        var source = revocation.Authorization;
        var user = await context.Users.Include(user => user.TotpCredential).SingleAsync(user => user.Id == userId, ct);
        var membership = await context.OrganizationMembers.SingleOrDefaultAsync(member =>
            member.UserId == userId && member.OrganizationId == organizationId, ct);
        if (source is null || source.Id != req.AuthorizationId || membership is null
            || membership.AuthorizationVersion != httpContext.User.GetAuthorizationVersion()
            || !source.SourceGeneration.AsSpan().SequenceEqual(req.SourceGeneration)
            || !source.IsSessionCurrent(user, session, membership, user.TotpCredential, now)
            || (source.LinkId is not null && (revocation.Link is null
                || !revocation.Link.AllowsTransfer(source.LinkEpoch ?? 0)
                || source.Sequence <= revocation.Link.LastInvalidationSequence))
            || !source.TryRecordActivity(Instant.FromUnixTimeMilliseconds(req.IdleDeadlineMs), now))
        {
            return SendConflictAsync(ct);
        }
        revocation.Fence(context);
        context.MarkPropertyAsUpdated(user, user => user.SharedUnlockSequence);
        context.MarkPropertyAsUpdated(session, session => session.RevokedAt);
        context.MarkPropertyAsUpdated(membership, member => member.Status);
        if (user.TotpCredential is { } factor)
        {
            context.MarkPropertyAsUpdated(factor, factor => factor.ConfigurationRevision);
        }
        if (!source.IsSessionCurrent(user, session, membership, user.TotpCredential, clock.GetCurrentInstant()))
        {
            return SendConflictAsync(ct);
        }
        try
        {
            await context.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return SendConflictAsync(ct);
        }
        return IdentityOperationResult<AuthorizeSharedUnlockResponse>.Ok(new AuthorizeSharedUnlockResponse(source.Id, source.Sequence, source.UserId,
            source.OrganizationId, source.CredentialRevision, source.PrivateKeyWrapRevision, source.AuthorizationVersion,
            source.UnlockedAt.ToUnixTimeMilliseconds(), source.IdleDeadline.ToUnixTimeMilliseconds(),
            source.AbsoluteDeadline.ToUnixTimeMilliseconds(), source.OfflineDeadline.ToUnixTimeMilliseconds()));
    }

    private static IdentityOperationResult<AuthorizeSharedUnlockResponse> SendConflictAsync(CancellationToken ct)
    {
        return IdentityOperationResult<AuthorizeSharedUnlockResponse>.Failure(StatusCodes.Status409Conflict, ErrorResponses.General("shared-unlock-authorization-conflict"));
    }
}
