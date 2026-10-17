using MassTransit;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Module.Notification.Contracts.Commands;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Infrastructure.Sharing;

namespace Palladin.Module.Vault.Features;

internal sealed class EntryShareOtpDispatcher(
    VaultDomainWriteContext context, EntryShareAuthority authority, EntryShareSecurity security,
    IBus bus, IClock clock)
{
    internal async Task DispatchAsync(Guid shareId, Guid sessionId, long generation, CancellationToken ct)
    {
        var session = await context.EntryShareSessions.SingleOrDefaultAsync(
            x => x.ShareId == shareId && x.Id == sessionId, ct);
        if (session is null || session.OtpGeneration != generation || session.ProtectedOtp is null)
        {
            return;
        }

        var share = await context.EntryShares.SingleOrDefaultAsync(x => x.Id == shareId, ct);
        var now = clock.GetCurrentInstant();
        try
        {
            if (share?.ProtectedRecipientEmail is null || session.OtpExpiresAt is null
                || session.OtpExpiresAt <= now || session.EmailVerifiedAt is not null)
            {
                throw new EntryShareUnavailableException();
            }

            share.EnsureSession(session, now);
            await authority.EnsureRecipientSourceAsync(share, ct);
        }
        catch (EntryShareUnavailableException)
        {
            session.ClearPendingOtp();
            await context.CommitAsync(ct);
            return;
        }

        session.FenceOtpDelivery();
        context.MarkPropertyAsUpdated(share, x => x.MutationVersion);
        await context.CommitAsync(ct);
        var message = new SendEntryShareVerificationEmailCommand(shareId, sessionId, generation,
            security.UnprotectRecipientEmail(share.Id, share.ProtectedRecipientEmail!),
            security.UnprotectOtp(shareId, sessionId, generation, session.ProtectedOtp), session.OtpLanguage!,
            session.OtpExpiresAt!.Value, now);
        // The consumer's in-memory outbox cannot acknowledge this durable handoff.
        await bus.Publish(message, send =>
            send.TimeToLive = (message.ExpiresAt - now).ToTimeSpan(), ct);

        context.Clear();
        var current = await context.EntryShareSessions.SingleOrDefaultAsync(
            x => x.ShareId == shareId && x.Id == sessionId, ct);
        if (current is not null && current.OtpGeneration == generation && current.ProtectedOtp is not null)
        {
            current.ClearPendingOtp();
            await context.CommitAsync(ct);
        }
    }
}
