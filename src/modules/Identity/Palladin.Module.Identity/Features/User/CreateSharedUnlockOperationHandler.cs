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

internal sealed class CreateSharedUnlockOperationHandler(IdentityDomainWriteContext context,
    IGuidProvider guidProvider, IClock clock, IOptionsMonitor<SharedUnlockOptions> options)
{
    public async Task<IdentityOperationResult<SharedUnlockOperationResponse>> ExecuteAsync(CreateSharedUnlockOperationRequest req, HttpContext httpContext, CancellationToken ct)
    {
        if (!options.CurrentValue.Enabled)
        {
            return IdentityOperationResult<SharedUnlockOperationResponse>.Status(StatusCodes.Status503ServiceUnavailable);
        }

        var userId = httpContext.User.GetUserId();
        var organizationId = httpContext.User.GetOrganizationId();
        var hash = TokenService.HashToken(req.RefreshToken);
        var session = await context.RefreshTokens.SingleOrDefaultAsync(token =>
            token.UserId == userId && token.OrganizationId == organizationId && token.TokenHash == hash, ct);
        if (session is null)
        {
            return IdentityOperationResult<SharedUnlockOperationResponse>.Status(401);
        }
        var direction = req.Direction == "web-to-extension"
            ? SharedUnlockDirection.WebToExtension : SharedUnlockDirection.ExtensionToWeb;
        var now = clock.GetCurrentInstant();
        var authority = await SharedUnlockOperationAuthority.LoadAsync(context, session, req.AuthorizationId,
            req.RecipientOrganizationId, req.LinkId, req.LinkEpoch, req.ExpectedPreferenceRevision,
            direction == SharedUnlockDirection.WebToExtension ? req.WebGeneration : req.ExtensionGeneration, now, ct);
        if (authority is null || authority.SourceMember.AuthorizationVersion != httpContext.User.GetAuthorizationVersion())
        {
            return SendUnavailableAsync(ct);
        }
        var idleDeadline = Instant.FromUnixTimeMilliseconds(req.IdleDeadlineMs);
        var absoluteDeadline = Instant.FromUnixTimeMilliseconds(req.AbsoluteDeadlineMs);
        var offlineDeadline = Instant.FromUnixTimeMilliseconds(req.OfflineDeadlineMs);
        if (idleDeadline <= now || absoluteDeadline <= now || offlineDeadline <= now)
        {
            return SendUnavailableAsync(ct);
        }
        var operation = SharedUnlockOperation.Create(guidProvider.Generate(), authority.User, authority.Source,
            session, authority.SourceOrganization, authority.TargetOrganization, authority.TargetMember, authority.Link,
            new SharedUnlockChannel(direction, req.ApiOrigin, req.WebOrigin, req.ExtensionId, req.DocumentBinding,
                req.WebGeneration, req.ExtensionGeneration, req.SourcePublicKey, req.RecipientPublicKey,
                req.RecipientProofPublicKey), SharedUnlockKeyContextDigest.Hash(authority.KeyContext),
            SharedUnlockTranscript.Challenge(), idleDeadline, absoluteDeadline, offlineDeadline,
            Instant.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds()));
        operation.BindTranscriptHash(SharedUnlockTranscript.Hash(operation));
        context.Add(operation);
        authority.Fence(context);
        if (clock.GetCurrentInstant() >= operation.ExpiresAt)
        {
            return SendUnavailableAsync(ct);
        }
        if (!options.CurrentValue.Enabled)
        {
            return IdentityOperationResult<SharedUnlockOperationResponse>.Status(StatusCodes.Status503ServiceUnavailable);
        }
        try
        {
            await context.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return SendUnavailableAsync(ct);
        }
        return IdentityOperationResult<SharedUnlockOperationResponse>.Ok(SharedUnlockOperationResponse.From(operation, authority.KeyContext));
    }

    private static IdentityOperationResult<SharedUnlockOperationResponse> SendUnavailableAsync(CancellationToken ct)
    {
        return IdentityOperationResult<SharedUnlockOperationResponse>.Failure(StatusCodes.Status409Conflict, ErrorResponses.General("shared-unlock-operation-unavailable"));
    }
}
