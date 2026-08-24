using Palladin.Core.Types.Exceptions;

namespace Palladin.Module.Vault.Domain;

internal sealed class ScriptExecutionScope
{
    internal Guid OrganizationId { get; private set; }
    internal Guid VaultId { get; private set; }
    internal Guid GrantId { get; private set; }
    internal Guid EntryId { get; private set; }
    internal ulong EntryRevision { get; private set; }
    internal bool IsScript { get; private set; }

    private ScriptExecutionScope() { }

    internal static ScriptExecutionScope Create(
        Guid organizationId,
        Guid vaultId,
        Guid grantId,
        Guid entryId,
        ulong entryRevision,
        bool isScript)
    {
        if (organizationId == Guid.Empty || vaultId == Guid.Empty || grantId == Guid.Empty
            || entryId == Guid.Empty || entryRevision == 0)
        {
            throw new DomainException("Script execution scope is invalid.");
        }

        return new ScriptExecutionScope
        {
            OrganizationId = organizationId,
            VaultId = vaultId,
            GrantId = grantId,
            EntryId = entryId,
            EntryRevision = entryRevision,
            IsScript = isScript,
        };
    }

    internal void RefreshFrom(ScriptExecutionScope replacement)
    {
        if (OrganizationId != replacement.OrganizationId || VaultId != replacement.VaultId
            || GrantId != replacement.GrantId || EntryId != replacement.EntryId)
        {
            throw new DomainException("Script execution scope identity cannot change during refresh.");
        }

        EntryRevision = replacement.EntryRevision;
        IsScript = replacement.IsScript;
    }
}
