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
internal sealed class RecordSharedUnlockActivityEndpoint(IdentityDomainWriteContext context, IClock clock)
    : Endpoint<RecordSharedUnlockActivityRequest, AuthorizeSharedUnlockResponse>
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
        var userId = User.GetUserId();
        var organizationId = User.GetOrganizationId();
        var hash = TokenService.HashToken(req.RefreshToken);
        var session = await context.RefreshTokens.SingleOrDefaultAsync(token => token.UserId == userId
            && token.OrganizationId == organizationId && token.TokenHash == hash, ct);
        var now = clock.GetCurrentInstant();
        if (session is null || !session.IsActive(now))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }
        var revocation = await SharedUnlockSessionRevocation.LoadAsync(context, session, ct);
        if (revocation.IsRevoked)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }
        var source = revocation.Authorization;
        var user = await context.Users.Include(user => user.TotpCredential).SingleAsync(user => user.Id == userId, ct);
        var membership = await context.OrganizationMembers.SingleOrDefaultAsync(member =>
            member.UserId == userId && member.OrganizationId == organizationId, ct);
        if (source is null || source.Id != req.AuthorizationId || membership is null
            || membership.AuthorizationVersion != User.GetAuthorizationVersion()
            || !source.SourceGeneration.AsSpan().SequenceEqual(req.SourceGeneration)
            || !source.IsSessionCurrent(user, session, membership, user.TotpCredential, now)
            || (source.LinkId is not null && (revocation.Link is null
                || !revocation.Link.AllowsTransfer(source.LinkEpoch ?? 0)
                || source.Sequence <= revocation.Link.LastInvalidationSequence))
            || !source.TryRecordActivity(Instant.FromUnixTimeMilliseconds(req.IdleDeadlineMs), now))
        {
            await SendConflictAsync(ct);
            return;
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
            await SendConflictAsync(ct);
            return;
        }
        try
        {
            await context.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await SendConflictAsync(ct);
            return;
        }
        await Send.OkAsync(new AuthorizeSharedUnlockResponse(source.Id, source.Sequence, source.UserId,
            source.OrganizationId, source.CredentialRevision, source.PrivateKeyWrapRevision, source.AuthorizationVersion,
            source.UnlockedAt.ToUnixTimeMilliseconds(), source.IdleDeadline.ToUnixTimeMilliseconds(),
            source.AbsoluteDeadline.ToUnixTimeMilliseconds(), source.OfflineDeadline.ToUnixTimeMilliseconds()), ct);
    }

    private async Task SendConflictAsync(CancellationToken ct)
    {
        AddError(ErrorResponses.General("shared-unlock-authorization-conflict"));
        await Send.ErrorsAsync(StatusCodes.Status409Conflict, ct);
    }
}
