using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using Palladin.Module.Identity.Infrastructure.Options;
using Palladin.Module.Identity.Infrastructure.Persistence;

namespace Palladin.Module.Identity.Infrastructure.Jwt;

internal sealed class RefreshTokenLineageRevoker(IdentityDomainWriteContext context,
    IOptions<JwtOptions> options)
{
    internal Task<bool> RevokeAfterReplayAsync(Guid userId, Guid tokenId, Instant now, CancellationToken ct) =>
        RevokeAsync(userId, tokenId, now, recordLogout: false, ct);

    internal Task<bool> RevokeForLogoutAsync(Guid userId, Guid tokenId, Instant now, CancellationToken ct) =>
        RevokeAsync(userId, tokenId, now, recordLogout: true, ct);

    private async Task<bool> RevokeAsync(Guid userId, Guid tokenId, Instant now, bool recordLogout, CancellationToken ct)
    {
        for (var attempt = 0; attempt < options.Value.RefreshTokenConcurrencyRetryLimit; attempt++)
        {
            var visited = new HashSet<Guid>();
            Guid? nextId = tokenId;
            while (nextId is { } id && visited.Add(id))
            {
                var next = await context.RefreshTokens.Where(token => token.UserId == userId && token.Id == id)
                    .Select(token => new { token.ReplacedByTokenId, token.RevokedAt }).SingleOrDefaultAsync(ct);
                if (next is null)
                {
                    break;
                }
                nextId = next.ReplacedByTokenId;
                if (next.RevokedAt is null)
                {
                    var token = await context.RefreshTokens.SingleAsync(token => token.UserId == userId && token.Id == id, ct);
                    nextId = token.ReplacedByTokenId;
                    if (token.IsRevoked)
                    {
                        continue;
                    }
                    if (recordLogout)
                    {
                        token.RevokeForLogout(now);
                    }
                    else
                    {
                        token.Revoke(now);
                    }
                }
            }
            try
            {
                await context.CommitAsync(ct);
                return true;
            }
            catch (DbUpdateConcurrencyException)
            {
                context.Clear();
            }
        }
        return false;
    }
}
