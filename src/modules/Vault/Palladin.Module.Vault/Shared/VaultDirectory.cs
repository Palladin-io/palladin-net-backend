using Palladin.Module.Vault.Infrastructure.Persistence;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;

namespace Palladin.Module.Vault.Shared;

[UsedImplicitly]
internal sealed class VaultDirectory(VaultDbReadContext readContext) : IVaultDirectory
{
    public Task<bool> IsMemberAsync(Guid vaultId, Guid userId, CancellationToken ct) =>
        readContext.VaultMembers.AnyAsync(m => m.VaultId == vaultId && m.UserId == userId, ct);

    public async Task<Guid?> GetOrganizationIdAsync(Guid vaultId, CancellationToken ct) =>
        await readContext.Vaults
            .Where(v => v.Id == vaultId)
            .Select(v => (Guid?)v.OrganizationId)
            .FirstOrDefaultAsync(ct);
}
