using Palladin.Core.Types.Exceptions;
using Palladin.Module.Vault.Domain;

namespace Palladin.Module.Vault.Infrastructure.Crypto;

internal static class ScriptExecutionGrantMaterialValidator
{
    internal static void Validate(
        ScriptExecutionPackage package,
        IReadOnlyCollection<ScriptExecutionScope> scopes,
        Guid organizationId,
        Guid vaultId,
        Guid grantId,
        Guid agentId,
        uint agentAccessEpoch,
        Guid scriptEntryId,
        Agent recipient,
        IReadOnlyDictionary<Guid, ulong> lockedRevisions)
    {
        var fingerprint = AgentRecipientKey.Fingerprint(recipient);
        if (package.OrganizationId != organizationId
            || package.VaultId != vaultId
            || package.GrantId != grantId
            || package.AgentId != agentId
            || package.AgentAccessEpoch != agentAccessEpoch
            || package.ScriptEntryId != scriptEntryId
            || package.PackageRevision != 1
            || package.RecipientAgentKeyVersion != recipient.RecipientKeyVersion
            || package.WrapperSuiteId != recipient.PublicKeySuiteId
            || !package.RecipientAgentKeyFingerprint.AsSpan().SequenceEqual(fingerprint)
            || scopes.Count != lockedRevisions.Count
            || scopes.Any(scope => !lockedRevisions.TryGetValue(scope.EntryId, out var revision)
                                   || scope.EntryRevision != revision)
            || !lockedRevisions.TryGetValue(scriptEntryId, out var scriptRevision)
            || package.ScriptRevision != scriptRevision)
        {
            throw new DomainException(
                "Script execution package binding is invalid or stale.");
        }
    }
}
