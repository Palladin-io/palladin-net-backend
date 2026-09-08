using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Module.Agents.Domain;
using Palladin.Module.Agents.Infrastructure.Persistence;
namespace Palladin.Module.Agents.Features;

internal sealed class AgentPairingClaims(AgentsDomainWriteContext domainWriteContext, IClock clock)
{
    public async Task<AgentPairingRequest?> ClaimAsync(Guid pairingId, Guid organizationId, CancellationToken ct)
    {
        var pairing = await domainWriteContext.AgentPairingRequests
            .FirstOrDefaultAsync(x => x.Id == pairingId, ct);
        var now = clock.GetCurrentInstant();
        if (pairing is null || !pairing.TryClaim(organizationId, now))
        {
            return null;
        }

        try
        {
            await domainWriteContext.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            domainWriteContext.Clear();
            pairing = await domainWriteContext.AgentPairingRequests
                .FirstOrDefaultAsync(x => x.Id == pairingId, ct);
            if (pairing is null
                || pairing.OrganizationId != organizationId
                || pairing.Status != AgentPairingStatus.Pending
                || pairing.IsExpired(now))
            {
                return null;
            }
        }
        return pairing;
    }
}
