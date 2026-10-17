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

using Palladin.Module.Identity.Shared;

namespace Palladin.Module.Identity.Features;

internal sealed class ActivateSharedUnlockLinkOperation(IdentityDomainWriteContext context, IClock clock)
{
    public async Task<IdentityOperationResult<SharedUnlockLinkResponse>> ExecuteAsync(ActivateSharedUnlockLinkRequest req, HttpContext httpContext, CancellationToken ct)
    {
        var userId = httpContext.User.GetUserId();
        var organizationId = httpContext.User.GetOrganizationId();
        var link = await context.SharedUnlockLinks.SingleOrDefaultAsync(
            link => link.UserId == userId && link.Id == req.LinkId, ct);
        if (link is null)
        {
            return IdentityOperationResult<SharedUnlockLinkResponse>.Status(404);
        }

        var hash = TokenService.HashToken(req.RefreshToken);
        var session = await context.RefreshTokens.SingleOrDefaultAsync(token =>
            token.UserId == userId && token.OrganizationId == organizationId && token.TokenHash == hash, ct);
        if (session is null || !session.IsActive(clock.GetCurrentInstant()))
        {
            return IdentityOperationResult<SharedUnlockLinkResponse>.Status(401);
        }

        var sessionId = session.SessionId ?? session.Id;
        var authorization = await context.SharedUnlockAuthorizations.SingleOrDefaultAsync(
            authorization => authorization.UserId == userId && authorization.SessionId == sessionId, ct);
        var user = await context.Users.Include(user => user.TotpCredential)
            .SingleAsync(user => user.Id == userId, ct);
        var membership = await context.OrganizationMembers.SingleOrDefaultAsync(member =>
            member.UserId == userId && member.OrganizationId == organizationId, ct);
        var now = clock.GetCurrentInstant();
        if (authorization is null || authorization.Id != req.AuthorizationId || membership is null
            || membership.AuthorizationVersion != httpContext.User.GetAuthorizationVersion()
            || !authorization.SourceGeneration.AsSpan().SequenceEqual(req.SourceGeneration)
            || !authorization.IsCurrent(user, session, membership, user.TotpCredential, now)
            || user.SharedUnlockRevision != req.ExpectedPreferenceRevision
            || link.Revision != req.ExpectedRevision || link.State == SharedUnlockLinkState.Revoked
            || (authorization.LinkId is not null
                && (authorization.LinkId != link.Id || authorization.LinkEpoch != link.Epoch))
            || (link.State == SharedUnlockLinkState.Locked
                && !link.TryActivateFromManualUnlock(req.ExpectedRevision, authorization.Sequence, now))
            || !authorization.TryBind(link))
        {
            return SendConflictAsync(ct);
        }

        context.MarkPropertyAsUpdated(user, user => user.SharedUnlockSequence);
        context.MarkPropertyAsUpdated(session, session => session.RevokedAt);
        context.MarkPropertyAsUpdated(membership, member => member.Status);
        context.MarkPropertyAsUpdated(authorization, authorization => authorization.Sequence);
        context.MarkPropertyAsUpdated(link, link => link.Revision);
        if (user.TotpCredential is { } factor)
        {
            context.MarkPropertyAsUpdated(factor, factor => factor.ConfigurationRevision);
        }

        try
        {
            await context.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return SendConflictAsync(ct);
        }

        return IdentityOperationResult<SharedUnlockLinkResponse>.Ok(new SharedUnlockLinkResponse(link.Id, link.Revision, link.Epoch, "active",
            link.LastInvalidationSequence, link.LastLogoutSequence));
    }

    private static IdentityOperationResult<SharedUnlockLinkResponse> SendConflictAsync(CancellationToken ct)
    {
        return IdentityOperationResult<SharedUnlockLinkResponse>.Failure(StatusCodes.Status409Conflict, ErrorResponses.General("shared-unlock-authorization-conflict"));
    }
}
