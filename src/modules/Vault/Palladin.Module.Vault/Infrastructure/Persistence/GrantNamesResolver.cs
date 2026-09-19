using Palladin.Module.Vault.Domain;
using Microsoft.EntityFrameworkCore;

namespace Palladin.Module.Vault.Infrastructure.Persistence;

internal static class GrantNamesResolver
{
    public static Task<GrantNames> ResolveAsync(
        this VaultDomainReadContext ctx, Guid? agentId, Guid? entryId,
        Guid vaultId, Guid? actorUserId, CancellationToken ct) =>
        ResolveAsync(ctx.Agents, ctx.Users, agentId, actorUserId, ct);

    public static Task<GrantNames> ResolveAsync(
        this VaultDomainWriteContext ctx, Guid? agentId, Guid? entryId,
        Guid vaultId, Guid? actorUserId, CancellationToken ct) =>
        ResolveAsync(ctx.Agents, ctx.Users, agentId, actorUserId, ct);

    private static async Task<GrantNames> ResolveAsync(
        IQueryable<Agent> agents, IQueryable<User> users,
        Guid? agentId, Guid? actorUserId, CancellationToken ct)
    {
        if (agentId is null)
        {
            var actorName = actorUserId == null
                ? null
                : await users
                    .Where(u => u.Id == actorUserId.Value)
                    .Select(u => (string?)u.DisplayName)
                    .FirstOrDefaultAsync(ct);

            return new GrantNames(GrantNames.UnknownAgent, null, string.Empty, actorName);
        }

        var raw = await agents
            .Where(a => a.Id == agentId.Value)
            .Select(a => new
            {
                AgentName = a.Name,
                ActorName = actorUserId == null
                    ? null
                    : users
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
