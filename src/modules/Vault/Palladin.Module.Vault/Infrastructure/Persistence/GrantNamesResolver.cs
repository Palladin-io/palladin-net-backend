using Palladin.Module.Vault.Domain;
using Microsoft.EntityFrameworkCore;

namespace Palladin.Module.Vault.Infrastructure.Persistence;

internal static class GrantNamesResolver
{
    public static async Task<GrantNames> ResolveAsync(
        this VaultDomainReadContext ctx,
        Guid? agentId,
        Guid? entryId,
        Guid vaultId,
        Guid? actorUserId,
        CancellationToken ct)
    {
        if (agentId is null)
        {
            var actorName = actorUserId == null
                ? null
                : await ctx.Users
                    .Where(u => u.Id == actorUserId.Value)
                    .Select(u => (string?)u.DisplayName)
                    .FirstOrDefaultAsync(ct);

            return new GrantNames(GrantNames.UnknownAgent, null, string.Empty, actorName);
        }

        var raw = await ctx.Agents
            .Where(a => a.Id == agentId.Value)
            .Select(a => new
            {
                AgentName = a.Name,
                ActorName = actorUserId == null
                    ? null
                    : ctx.Users
                        .Where(u => u.Id == actorUserId.Value)
                        .Select(u => (string?)u.DisplayName)
                        .FirstOrDefault(),
            })
            .FirstOrDefaultAsync(ct);

        return new GrantNames(
            raw?.AgentName ?? GrantNames.UnknownAgent,
            null,
            string.Empty,
            raw?.ActorName);
    }
}
