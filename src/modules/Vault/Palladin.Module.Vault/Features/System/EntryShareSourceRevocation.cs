using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Types;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Persistence;

namespace Palladin.Module.Vault.Features;

internal static class EntryShareSourceRevocation
{
    internal static async Task RevokeAsync(VaultDomainWriteContext context,
        IQueryable<EntryShare> affectedShares, int batchSize, Instant now, CancellationToken ct)
    {
        context.Clear();
        Guid? afterId = null;
        Guid? afterSender = null;
        while (true)
        {
            var query = affectedShares.Where(x => x.RevokedAt == null && x.ExpiredAt == null);
            if (afterId is not null)
            {
                query = query.Where(x => x.CreatedBy.CompareTo(afterSender!.Value) > 0
                    || (x.CreatedBy == afterSender.Value && x.Id.CompareTo(afterId.Value) > 0));
            }

            var shares = await query.OrderBy(x => x.CreatedBy).ThenBy(x => x.Id).Take(batchSize).ToListAsync(ct);
            if (shares.Count == 0)
            {
                return;
            }

            foreach (var share in shares)
            {
                share.Revoke(EntryShareActivityKind.SourceAccessRemoved, now);
            }

            await context.CommitAsync(ct);
            afterId = shares[^1].Id;
            afterSender = shares[^1].CreatedBy;
            context.Clear();
        }
    }
}
