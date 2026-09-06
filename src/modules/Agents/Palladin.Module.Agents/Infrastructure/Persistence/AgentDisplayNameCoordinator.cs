using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Module.Agents.Domain;

namespace Palladin.Module.Agents.Infrastructure.Persistence;

/// <summary>
/// Fences the cross-table invariant between visible Agent names and short-lived pairing reservations.
/// Every operation that assigns a name advances the same organization row in its normal domain commit,
/// so a racing decision rolls the complete write back through optimistic concurrency.
/// </summary>
internal sealed class AgentDisplayNameCoordinator(AgentsDomainWriteContext context)
{
    internal async Task FenceAsync(Guid organizationId, CancellationToken ct)
    {
        var fence = await context.AgentDisplayNameFences
            .FirstOrDefaultAsync(x => x.OrganizationId == organizationId, ct);
        if (fence is null)
        {
            context.Add(AgentDisplayNameFence.Create(organizationId));
            return;
        }

        fence.Advance();
    }

    internal async Task<bool> IsAvailableAsync(
        Guid organizationId,
        string displayName,
        Instant now,
        Guid? exceptAgentId,
        Guid? exceptPairingId,
        CancellationToken ct)
    {
        var comparison = displayName.ToUpperInvariant();
        var nameKey = AgentMetadata.DisplayNameReservationKey(displayName);
        var usedByAgent = await context.Agents.AnyAsync(
            x => x.OrganizationId == organizationId
                 && (!exceptAgentId.HasValue || x.Id != exceptAgentId.Value)
                 && x.Name != null
                 && (x.NameKey == nameKey
                     // The fallback preserves rows created before the
                     // name-key migration. Exact comparison also closes any
                     // historical or provider-specific Unicode casing gap.
                     || x.Name == displayName
                     || x.Name.ToUpper() == comparison),
            ct);
        if (usedByAgent)
        {
            return false;
        }

        var reservationKey = AgentMetadata.DisplayNameReservationKey(displayName);
        return !await context.AgentPairingRequests.AnyAsync(
            x => x.OrganizationId == organizationId
                 && (!exceptPairingId.HasValue || x.Id != exceptPairingId.Value)
                 && x.Status == AgentPairingStatus.Pending
                 && x.ExpiresAt > now
                 && x.ReservedDisplayNameKey == reservationKey,
            ct);
    }
}
