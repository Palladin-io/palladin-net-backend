using Palladin.Core.Persistence;
using Palladin.Module.Notification.Domain;
using Palladin.Module.Notification.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;

namespace Palladin.Module.Notification.Infrastructure.Email;

internal sealed class EmailDispatchDeduplicator(
    NotificationDbWriteContext writeContext,
    NotificationDomainWriteContext domainWriteContext,
    IEmailSender emailSender,
    IClock clock) : IEmailDispatchDeduplicator
{
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

        await using var transaction = await writeContext.Database.BeginTransactionAsync(ct);
        domainWriteContext.Add(EmailDelivery.Create(idempotencyKey, clock.GetCurrentInstant()));
        try
        {
            await domainWriteContext.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        { SqlState: Palladin.Core.Persistence.PostgresErrorCodes.UniqueViolation })
        {
            return;
        }

        await emailSender.SendAsync(message, ct);
        await transaction.CommitAsync(ct);
    }
}
