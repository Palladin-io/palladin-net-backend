using Palladin.Module.Vault.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Palladin.Module.Vault.Triggers;

internal sealed record AgentNotificationFields(string? PublicKey, string? IconKey, string? IconColor);

internal static class AgentNotificationLookup
{
    internal static async Task<AgentNotificationFields> ResolveAsync(
        VaultDomainReadContext readContext,
        Guid organizationId,
        Guid agentId,
        CancellationToken cancellationToken)
    {
        var fields = await readContext.Agents
            .Where(a => a.OrganizationId == organizationId && a.Id == agentId)
            .Select(a => new { a.PublicKey, a.IconKey, a.IconColor })
            .FirstOrDefaultAsync(cancellationToken);

        if (fields is null)
        {
            return new AgentNotificationFields(null, null, null);
        }

        return new AgentNotificationFields(
            string.IsNullOrWhiteSpace(fields.PublicKey) ? null : fields.PublicKey,
            string.IsNullOrWhiteSpace(fields.IconKey) ? null : fields.IconKey,
            string.IsNullOrWhiteSpace(fields.IconColor) ? null : fields.IconColor);
    }
}
