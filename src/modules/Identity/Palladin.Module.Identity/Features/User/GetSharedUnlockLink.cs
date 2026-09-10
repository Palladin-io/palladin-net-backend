using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Palladin.Core.Security;
using Palladin.Module.Identity.Infrastructure.Persistence;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record GetSharedUnlockLinkRequest(Guid LinkId);

[PublicAPI]
public sealed record SharedUnlockLinkResponse(Guid LinkId, uint Revision, uint Epoch, string State,
    uint LastInvalidationSequence, uint LastLogoutSequence);

[PublicAPI]
internal sealed class GetSharedUnlockLinkEndpoint(IdentityDomainReadContext context)
    : Endpoint<GetSharedUnlockLinkRequest, SharedUnlockLinkResponse>
{
    public override void Configure()
    {
        Get("api/account/shared-unlock/links/{LinkId}");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Tags("Identity/Account");
        Summary(summary => summary.Summary = "Get this account's local shared-unlock link authority");
    }

    public override async Task HandleAsync(GetSharedUnlockLinkRequest req, CancellationToken ct)
    {
        var userId = User.GetUserId();
        var link = await context.SharedUnlockLinks
            .Where(link => link.UserId == userId && link.Id == req.LinkId)
            .Select(link => new { link.Id, link.Revision, link.Epoch, link.State, link.LastInvalidationSequence, link.LastLogoutSequence })
            .SingleOrDefaultAsync(ct);
        if (link is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        HttpContext.Response.Headers.CacheControl = "no-store";
        await Send.OkAsync(new SharedUnlockLinkResponse(link.Id, link.Revision, link.Epoch,
            link.State.ToString().ToLowerInvariant(), link.LastInvalidationSequence, link.LastLogoutSequence), ct);
    }
}
