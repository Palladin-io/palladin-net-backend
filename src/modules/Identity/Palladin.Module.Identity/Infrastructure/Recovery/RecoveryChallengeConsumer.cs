using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure.Persistence;

namespace Palladin.Module.Identity.Infrastructure.Recovery;

// Committed before the recovery write so a presented proof is burned even if that write fails.
internal sealed class RecoveryChallengeConsumer(
    IdentityDomainWriteContext domainWriteContext,
    IClock clock,
    IOptions<AccountRecoveryOptions> options)
{
    public async Task<bool> ConsumeAsync(User user, byte[]? proof, CancellationToken ct)
    {
        var presentedChallengeHash = user.RecoveryChallengeHash;
        for (var attempt = 1; ; attempt++)
        {
            var isValid = user.ConsumeRecoveryChallenge(proof, clock.GetCurrentInstant());
            try
            {
                await domainWriteContext.CommitAsync(ct);
                return isValid;
            }
            catch (DbUpdateConcurrencyException) when (attempt < options.Value.ConsumeConcurrencyRetryLimit)
            {
                await domainWriteContext.ReloadAsync(user, ct);
                // An unrelated account write rolled the burn back. Retry only while the presented challenge is
                // still outstanding: a concurrent consumer already burned it, and a replacement must stay usable.
                if (!IsSameChallenge(presentedChallengeHash, user.RecoveryChallengeHash))
                {
                    return false;
                }
            }
            catch (DbUpdateConcurrencyException)
            {
                return false;
            }
        }
    }

    private static bool IsSameChallenge(byte[]? presented, byte[]? current) =>
        presented is not null && current is not null && presented.AsSpan().SequenceEqual(current);
}
