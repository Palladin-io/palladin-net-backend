using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure.Persistence;

namespace Palladin.Module.Identity.Infrastructure.Waitlist;

internal sealed class WaitlistDeveloperBenefitActivator(
    IdentityDomainWriteContext domainWriteContext)
{
    // Every caller must hold an explicit transaction until its domain commit. Locking the shared
    // waitlist row serializes account verification, waitlist verification, OAuth account creation,
    // and history fallback so two independently committed opt-ins cannot both miss activation.
    internal async Task<Instant?> TryActivateAsync(
        User user,
        Instant now,
        CancellationToken cancellationToken)
    {
        if (user.WaitlistDeveloperBenefitStartedAt is not null)
        {
            return user.ActiveWaitlistDeveloperBenefitEndsAt(now);
        }

        var entry = await LockEntryAsync(user.Email, cancellationToken);

        // Another session may have claimed the entry while this request waited for the row lock.
        // Refresh the already-tracked aggregate before issuing a session so both contenders see
        // the committed benefit without producing a second activation event.
        if (entry?.DeveloperBenefitUserId == user.Id
            && user.WaitlistDeveloperBenefitStartedAt is null)
        {
            await domainWriteContext.ReloadAsync(user, cancellationToken);
        }

        return TryActivate(user, entry, now);
    }

    internal async Task<Instant?> TryActivateAsync(
        WaitlistEntry entry,
        Instant now,
        CancellationToken cancellationToken)
    {
        var lockedEntry = await LockEntryAsync(entry.Email, cancellationToken);
        var user = await domainWriteContext.Users
            .FirstOrDefaultAsync(candidate => candidate.Email == entry.Email, cancellationToken);
        return TryActivate(user, lockedEntry, now);
    }

    private async Task<WaitlistEntry?> LockEntryAsync(
        string normalizedEmail,
        CancellationToken cancellationToken)
    {
        var entries = await domainWriteContext
            .FromSqlInterpolated<WaitlistEntry>(
                $"""
                 SELECT *
                 FROM "WaitlistEntries"
                 WHERE "Email" = {normalizedEmail}
                 FOR UPDATE
                 """)
            .ToListAsync(cancellationToken);
        return entries.SingleOrDefault();
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
