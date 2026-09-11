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

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record GetSharedUnlockSessionStateRequest
{
    public string RefreshToken { get; init; } = string.Empty;
    public Guid LinkId { get; init; }
}

[PublicAPI]
public sealed record SharedUnlockSessionStateResponse(string Action, SharedUnlockLinkResponse? Link);

[UsedImplicitly]
internal sealed class GetSharedUnlockSessionStateValidator : Validator<GetSharedUnlockSessionStateRequest>
{
    public GetSharedUnlockSessionStateValidator()
    {
        RuleFor(request => request.RefreshToken).NotEmpty().MaximumLength(1024);
        RuleFor(request => request.LinkId).NotEmpty();
    }
}

[PublicAPI]
internal sealed class GetSharedUnlockSessionStateEndpoint(IdentityDomainReadContext context, IClock clock)
    : Endpoint<GetSharedUnlockSessionStateRequest, SharedUnlockSessionStateResponse>
{
    public override void Configure()
    {
        Post("api/account/shared-unlock/session-state");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Options(builder => builder.AllowNonActiveOrganizationMembership());
        Tags("Identity/Account");
        Summary(summary =>
        {
            summary.Summary = "Read closing actions for this client's own logical session";
            summary.Description = "Read-only POST keeps the own refresh token out of URLs. No action is unlock authority. "
                + "The response neither renews a session nor returns its source authorization or generation.";
        });
    }

    public override async Task HandleAsync(GetSharedUnlockSessionStateRequest req, CancellationToken ct)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        var userId = User.GetUserId();
        var organizationId = User.GetOrganizationId();
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
            || own.AuthorizationVersion != User.GetAuthorizationVersion())
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var action = own.Root?.LinkId != req.LinkId ? "none"
            : own.Link is null || own.Root.Sequence <= own.Link.LastLogoutSequence ? "logout"
            : own.Root.Sequence <= own.Link.LastInvalidationSequence ? "lock" : "none";
        var responseLink = own.Link is { } link
            ? new SharedUnlockLinkResponse(link.Id, link.Revision, link.Epoch, link.State.ToString().ToLowerInvariant(),
                link.LastInvalidationSequence, link.LastLogoutSequence)
            : null;
        await Send.OkAsync(new SharedUnlockSessionStateResponse(action, responseLink), ct);
    }
}
