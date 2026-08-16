using Palladin.Module.Notification.Domain;
using Palladin.Module.Notification.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;
using Palladin.Core.Guid;

namespace Palladin.Module.Notification.Infrastructure.Email;

internal sealed class EmailDispatchDeduplicator(
    NotificationDomainWriteContext domainWriteContext,
    IEmailSender emailSender,
    IClock clock,
    IGuidProvider guidProvider) : IEmailDispatchDeduplicator
{
    private static readonly Duration DispatchLease = Duration.FromSeconds(4);

    public async Task SendOnceAsync(
        string? idempotencyKey,
        EmailMessage message,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            await emailSender.SendAsync(message, ct);
            return;
        }

        var now = clock.GetCurrentInstant();
        var dispatchToken = guidProvider.Generate();
        var delivery = await domainWriteContext.EmailDeliveries.SingleOrDefaultAsync(
            x => x.IdempotencyKey == idempotencyKey,
            ct);
        if (delivery is null)
        {
            delivery = EmailDelivery.Claim(idempotencyKey, dispatchToken, now, DispatchLease);
            domainWriteContext.Add(delivery);
            try
            {
                await domainWriteContext.CommitAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException
                   { SqlState: Palladin.Core.Persistence.PostgresErrorCodes.UniqueViolation })
            {
                domainWriteContext.Clear();
                delivery = await domainWriteContext.EmailDeliveries.SingleAsync(
                    x => x.IdempotencyKey == idempotencyKey,
                    ct);
            }
        }

        if (delivery.Status == EmailDeliveryStatus.Sent)
        {
            return;
        }

        if (delivery.DispatchToken != dispatchToken)
        {
            if (!delivery.TryReclaim(dispatchToken, now, DispatchLease))
            {
                throw new EmailDispatchInProgressException();
            }

            try
            {
                await domainWriteContext.CommitAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                throw new EmailDispatchInProgressException();
            }
        }

        try
        {
            // SES is never called while a database transaction or row lock is held. A crash after
            // provider acceptance can still cause a retry (SES has no idempotency token), so the
            // delivery contract is explicitly at-least-once in that narrow failure window.
            await emailSender.SendAsync(message, ct);
        }
        catch
        {
            domainWriteContext.Clear();
            delivery = await domainWriteContext.EmailDeliveries.SingleOrDefaultAsync(
                x => x.IdempotencyKey == idempotencyKey && x.DispatchToken == dispatchToken,
                CancellationToken.None);
            if (delivery is not null)
            {
                domainWriteContext.Remove(delivery);
                await domainWriteContext.CommitAsync(CancellationToken.None);
            }
            throw;
        }

        domainWriteContext.Clear();
        delivery = await domainWriteContext.EmailDeliveries.SingleAsync(
            x => x.IdempotencyKey == idempotencyKey,
            ct);
        delivery.MarkSent(dispatchToken, clock.GetCurrentInstant());
        await domainWriteContext.CommitAsync(ct);
    }
}

internal sealed class EmailDispatchInProgressException()
    : Exception("The idempotent email dispatch is already in progress.");
