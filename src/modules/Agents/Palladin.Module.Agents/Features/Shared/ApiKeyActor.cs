using Palladin.Module.Agents.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Palladin.Module.Agents.Features;

// API-key audit rows are attributed to the acting user. The display name is denormalized at write
// time from the module's User replica so the Audit module never has to resolve it later.
internal static class ApiKeyActor
{
    internal static async Task<string> ResolveActorNameAsync(
        AgentsDomainReadContext domainReadContext,
        Guid userId,
        CancellationToken ct) =>
        await domainReadContext.Users
            .Where(u => u.Id == userId)
            .Select(u => u.DisplayName)
            .FirstOrDefaultAsync(ct) ?? string.Empty;
}
