using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure.Persistence;

namespace Palladin.Module.Identity.Infrastructure.Waitlist;

internal sealed class WaitlistDeveloperBenefitActivator(
    IdentityDomainWriteContext domainWriteContext)
{
    internal async Task<Instant?> TryActivateAsync(
        User user,
        Instant now,
        CancellationToken cancellationToken)
    {
        if (!user.EmailVerified)
        {
            return null;
        }

        if (user.WaitlistDeveloperBenefitStartedAt is not null)
        {
            return user.ActiveWaitlistDeveloperBenefitEndsAt(now);
        }

        var entry = await domainWriteContext.WaitlistEntries
            .FirstOrDefaultAsync(candidate => candidate.Email == user.Email, cancellationToken);
        return TryActivate(user, entry, now);
    }

    internal async Task<Instant?> TryActivateAsync(
        WaitlistEntry entry,
        Instant now,
        CancellationToken cancellationToken)
    {
        var user = await domainWriteContext.Users
            .FirstOrDefaultAsync(candidate => candidate.Email == entry.Email, cancellationToken);
        return TryActivate(user, entry, now);
    }

    private static Instant? TryActivate(User? user, WaitlistEntry? entry, Instant now)
    {
        if (user is null || !user.EmailVerified || entry is null)
        {
            return null;
        }

        var endsAt = entry.ClaimDeveloperBenefit(user.Id, user.CreatedAt, now);
        if (endsAt is null)
        {
            return user.ActiveWaitlistDeveloperBenefitEndsAt(now);
        }

        user.ActivateWaitlistDeveloperBenefit(now, endsAt.Value);
        return endsAt;
    }
}
