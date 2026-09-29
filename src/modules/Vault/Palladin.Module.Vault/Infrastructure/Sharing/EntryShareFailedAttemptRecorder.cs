using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using Palladin.Core.Types;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Persistence;

namespace Palladin.Module.Vault.Infrastructure.Sharing;

internal sealed class EntryShareFailedAttemptRecorder(
    VaultDomainWriteContext context, EntryShareReceiver receiver,
    IOptions<EntrySharingOptions> options, IClock clock)
{
    internal Task RecordSecretAsync(EntryShare share, EntryShareSession session, string sessionToken, CancellationToken ct) =>
        RecordAsync(share, session, sessionToken,
            static (current, receipt, _) => current.Protection != EntryShareProtection.None
                && receipt.SecretVerifiedAt is null, ct);

    internal Task RecordOtpAsync(EntryShare share, EntryShareSession session, string sessionToken, long generation, CancellationToken ct) =>
        RecordAsync(share, session, sessionToken,
            (current, receipt, now) => current.RecipientMode == EntryShareRecipientMode.NamedRecipient
                && receipt.OtpGeneration == generation && receipt.EmailVerifiedAt is null
                && receipt.OtpExpiresAt is { } expiry && now < expiry, ct);

    private async Task RecordAsync(EntryShare share, EntryShareSession session, string sessionToken,
        Func<EntryShare, EntryShareSession, Instant, bool> sameGate, CancellationToken ct)
    {
        var securityVersion = share.SecurityVersion;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var now = clock.GetCurrentInstant();
            if (share.SecurityVersion != securityVersion || !sameGate(share, session, now))
            {
                throw new EntryShareUnavailableException();
            }

            share.EnsureSession(session, now);
            share.RegisterFailedAttempt(now, options.Value.FailedAttemptLimit,
                options.Value.TotalFailedAttemptLimit, Duration.FromSeconds(options.Value.LockoutSeconds));
            try
            {
                await context.CommitAsync(ct);
                return;
            }
            catch (DbUpdateConcurrencyException)
            {
                context.Clear();
                (share, session) = await receiver.LoadSessionAsync(share.Id, session.Id, sessionToken, ct);
            }
        }
    }
}
