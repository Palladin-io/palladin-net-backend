using Microsoft.Extensions.Options;
using NodaTime;
using Palladin.Module.Identity.Domain;

namespace Palladin.Module.Identity.Infrastructure.Waitlist;

internal sealed class WaitlistBenefitPolicy(IOptions<WaitlistOptions> options)
{
    internal bool TryReserve(WaitlistEntry? entry, Guid userId, Instant now)
    {
        var configured = options.Value;
        if (!configured.BenefitEnabled
            || configured.PublicLaunchAtUtc is null
            || configured.BenefitClaimDeadlineAtUtc is null)
        {
            return false;
        }

        return entry?.TryReserveDeveloperBenefit(
            userId,
            Instant.FromDateTimeOffset(configured.PublicLaunchAtUtc.Value),
            Instant.FromDateTimeOffset(configured.BenefitClaimDeadlineAtUtc.Value),
            configured.BenefitDurationMonths,
            configured.PromotionTermsVersion,
            now) ?? false;
    }
}
