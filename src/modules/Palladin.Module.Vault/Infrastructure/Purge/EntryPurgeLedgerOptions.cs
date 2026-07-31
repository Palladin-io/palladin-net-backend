namespace Palladin.Module.Vault.Infrastructure.Purge;

internal sealed class EntryPurgeLedgerOptions
{
    public const string Position = "Modules:Vault:PurgeLedger";

    public bool Enabled { get; init; } = true;
    public string BucketName { get; init; } = string.Empty;
    public string Region { get; init; } = "eu-west-1";
    public string ObjectPrefix { get; init; } = "vault-entry-purge-ledger";
    public string KeyParameterPrefix { get; init; } = string.Empty;
    public uint CurrentKeyVersion { get; init; } = 1;
    public int RetentionDays { get; init; } = 120;
}
