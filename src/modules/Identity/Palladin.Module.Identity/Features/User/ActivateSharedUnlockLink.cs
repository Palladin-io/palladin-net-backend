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

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record ActivateSharedUnlockLinkRequest
{
    public Guid LinkId { get; init; }
    public Guid AuthorizationId { get; init; }
    public string RefreshToken { get; init; } = string.Empty;
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))]
    public byte[] SourceGeneration { get; init; } = [];
    public uint ExpectedRevision { get; init; }
    public uint ExpectedPreferenceRevision { get; init; }
}

[UsedImplicitly]
internal sealed class ActivateSharedUnlockLinkValidator : Validator<ActivateSharedUnlockLinkRequest>
{
    public ActivateSharedUnlockLinkValidator()
    {
        RuleFor(request => request.LinkId).NotEmpty();
        RuleFor(request => request.AuthorizationId).NotEmpty();
        RuleFor(request => request.RefreshToken).NotEmpty().MaximumLength(1024);
        RuleFor(request => request.SourceGeneration).Must(value => value is { Length: 32 });
        RuleFor(request => request.ExpectedRevision).GreaterThan(0u);
        RuleFor(request => request.ExpectedPreferenceRevision).GreaterThan(0u);
    }
}

[PublicAPI]
internal sealed class ActivateSharedUnlockLinkEndpoint(IdentityDomainWriteContext context, IClock clock)
    : Endpoint<ActivateSharedUnlockLinkRequest, SharedUnlockLinkResponse>
{
    public override void Configure()
    {
        Post("api/account/shared-unlock/links/{LinkId}/activate");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Tags("Identity/Account");
        Summary(summary => summary.Summary = "Bind current manual-unlock authority to a verified local browser link");
    }

    public override async Task HandleAsync(ActivateSharedUnlockLinkRequest req, CancellationToken ct)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        var userId = User.GetUserId();
        var organizationId = User.GetOrganizationId();
        var link = await context.SharedUnlockLinks.SingleOrDefaultAsync(
            link => link.UserId == userId && link.Id == req.LinkId, ct);
        if (link is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var hash = TokenService.HashToken(req.RefreshToken);
        var session = await context.RefreshTokens.SingleOrDefaultAsync(token =>
            token.UserId == userId && token.OrganizationId == organizationId && token.TokenHash == hash, ct);
        if (session is null || !session.IsActive(clock.GetCurrentInstant()))
        {
            await Send.UnauthorizedAsync(ct);
            return;
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
            || membership.AuthorizationVersion != User.GetAuthorizationVersion()
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
            await SendConflictAsync(ct);
            return;
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
            await SendConflictAsync(ct);
            return;
        }

        await Send.OkAsync(new SharedUnlockLinkResponse(link.Id, link.Revision, link.Epoch, "active",
            link.LastInvalidationSequence, link.LastLogoutSequence), ct);
    }

    private async Task SendConflictAsync(CancellationToken ct)
    {
        AddError(ErrorResponses.General("shared-unlock-authorization-conflict"));
        await Send.ErrorsAsync(StatusCodes.Status409Conflict, ct);
    }
}
