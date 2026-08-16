using Palladin.Module.Notification.Domain;
using Palladin.Module.Notification.Infrastructure.Persistence;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;

namespace Palladin.Module.Notification.Infrastructure.Email.Suppression;

[UsedImplicitly]
internal sealed class EmailSuppressionStore(
    NotificationDbReadContext readContext,
    NotificationDomainWriteContext domainWriteContext,
    IClock clock) : IEmailSuppressionStore
{
    public Task<bool> IsSuppressedAsync(string address, CancellationToken ct) =>
        readContext.SuppressedEmails.AnyAsync(x => x.Address == Normalize(address), ct);

    public async Task SuppressAsync(string address, SuppressionReason reason, CancellationToken ct)
    {
        var normalized = Normalize(address);
        if (await domainWriteContext.SuppressedEmails.AnyAsync(x => x.Address == normalized, ct))
        {
            return;
        }

        var entity = SuppressedEmail.Create(normalized, reason, clock.GetCurrentInstant());
        domainWriteContext.Add(entity);
        try
        {
            await domainWriteContext.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: Core.Persistence.PostgresErrorCodes.UniqueViolation })
        {
            // A concurrent poller already suppressed this address — the row exists, so this is success.
            domainWriteContext.Clear();
        }
    }

    private static string Normalize(string address) => address.Trim().ToLowerInvariant();
}
