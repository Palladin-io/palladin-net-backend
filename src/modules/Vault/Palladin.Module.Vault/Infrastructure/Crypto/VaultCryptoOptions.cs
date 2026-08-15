namespace Palladin.Module.Vault.Infrastructure.Crypto;

internal sealed class VaultCryptoOptions
{
    public const string Position = "Modules:Vault:Crypto";

    public int MaxWrappedVaultKeyBytes { get; init; } = 4096;

    // Must stay >= MaxEntryBlobBytes + secretbox overhead: grant material is the entry plaintext
    // re-encrypted under a per-grant DEK, so any blob that passes entry validation must also fit here.
    public int MaxReEncryptedBlobBytes { get; init; } = 66560;
    public int MaxNonceBytes { get; init; } = 64;
    public int MaxAgentWrappedDekBytes { get; init; } = 4096;
    public int MaxGrantEntriesPerGrant { get; init; } = 500;
    public int MaxFullGrantPreparationBatchEntries { get; init; } = 100;
    public int MaxFullGrantMaterialPageSize { get; init; } = 100;
    public int FullGrantPreparationTtlMinutes { get; init; } = 15;
    public int FullGrantPreparationCleanupBatchSize { get; init; } = 100;
    public int MaxEntryBlobBytes { get; init; } = 65536;
}
