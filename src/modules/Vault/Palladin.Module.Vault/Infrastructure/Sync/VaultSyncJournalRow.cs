namespace Palladin.Module.Vault.Infrastructure.Sync;

internal sealed class VaultSyncJournalRow
{
    public Guid EntryId { get; init; }
    public decimal Sequence { get; init; }
}
