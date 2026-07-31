using JetBrains.Annotations;

namespace Palladin.Module.Vault.Shared;

// Cross-module read-only contract over vault membership/ownership. Used by the Audit module for
// read authorization and to resolve a vault's organization for events that do not carry it.
// A synchronous check is low-risk here (read-path authz of an audit query) — we deliberately do NOT
// replicate VaultMember into Audit.
[PublicAPI]
public interface IVaultDirectory
{
    Task<bool> IsMemberAsync(Guid vaultId, Guid userId, CancellationToken ct);

    // Returns the owning organization of a vault, or null if the vault does not exist.
    Task<Guid?> GetOrganizationIdAsync(Guid vaultId, CancellationToken ct);
}
