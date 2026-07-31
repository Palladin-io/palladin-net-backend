namespace Palladin.Module.Vault.Domain;

// Transitional grant-event display metadata pending completion of the opaque grant-data cutover.
// VaultName is intentionally always empty: Vault display metadata is encrypted and must never be
// reconstructed by the backend. Do not add a server-side Vault name resolver here.
public sealed record GrantNames(
    string AgentName,
    string? EntryLabel,
    string VaultName,
    string? ActorName)
{
    // Fallbacks used when a name cannot be resolved (deleted/missing replica row). System actor label
    // for transitions performed without a user (e.g. cascade revoke).
    public const string UnknownAgent = "Unknown agent";
    public const string UnknownEntry = "a credential";
    public const string SystemActor = "System";
}
