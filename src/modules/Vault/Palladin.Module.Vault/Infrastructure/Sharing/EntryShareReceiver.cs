using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Persistence;

namespace Palladin.Module.Vault.Infrastructure.Sharing;

internal sealed class EntryShareReceiver(
    VaultDomainWriteContext context, EntryShareAuthority authority, EntryShareSecurity security, IClock clock)
{
    internal async Task<(EntryShare Share, EntryShareSession Session)> LoadSessionAsync(
        Guid shareId, Guid sessionId, string token, CancellationToken ct)
    {
        var session = await context.EntryShareSessions.SingleOrDefaultAsync(
            x => x.ShareId == shareId && x.Id == sessionId, ct);
        if (session is null || !security.VerifySessionToken(sessionId, token, session.TokenHash))
        {
            throw new EntryShareUnavailableException();
        }

        var share = await context.EntryShares.SingleOrDefaultAsync(x => x.Id == shareId, ct);
        if (share is null)
        {
            throw new EntryShareUnavailableException();
        }

        share.EnsureSession(session, clock.GetCurrentInstant());
        await authority.EnsureRecipientSourceAsync(share, ct);
        return (share, session);
    }
}
