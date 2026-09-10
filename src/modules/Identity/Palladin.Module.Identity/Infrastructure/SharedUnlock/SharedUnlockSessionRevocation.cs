using Microsoft.EntityFrameworkCore;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure.Persistence;

namespace Palladin.Module.Identity.Infrastructure.SharedUnlock;

internal sealed record SharedUnlockSessionRevocation(SharedUnlockAuthorization? Authorization, SharedUnlockLink? Link)
{
    internal bool IsRevoked => Authorization is { LinkId: not null }
        && (Link is null || Link.RevokesSession(Authorization));

    internal static async Task<SharedUnlockSessionRevocation> LoadAsync(IdentityDomainWriteContext context,
        RefreshToken session, CancellationToken ct)
    {
        var sessionId = session.SessionId ?? session.Id;
        var authorization = await context.SharedUnlockAuthorizations.SingleOrDefaultAsync(root =>
            root.UserId == session.UserId && root.SessionId == sessionId, ct);
        var link = authorization?.LinkId is { } linkId
            ? await context.SharedUnlockLinks.SingleOrDefaultAsync(link => link.UserId == session.UserId && link.Id == linkId, ct)
            : null;
        return new SharedUnlockSessionRevocation(authorization, link);
    }

    internal void Fence(IdentityDomainWriteContext context)
    {
        if (Authorization is not null)
        {
            context.MarkPropertyAsUpdated(Authorization, root => root.Sequence);
        }
        if (Link is not null)
        {
            context.MarkPropertyAsUpdated(Link, link => link.Revision);
        }
    }
}
