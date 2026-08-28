using NodaTime;
using Palladin.Core.Types;
using Palladin.Core.Types.Exceptions;

namespace Palladin.Module.Vault.Domain;

internal sealed class ScriptExecutionGrant : Grant
{
    public Guid ScriptEntryId { get; private set; }

    public override GrantType Type => GrantType.ScriptExecution;

    // A ScriptExecution grant authorizes only the parent execution operation. Its reference scopes
    // can be opened solely as part of that one package and never become independent Entry access.
    public override bool Covers(Guid entryId) => entryId == ScriptEntryId && ScriptExecutionPackage is not null;

    private ScriptExecutionGrant() { }

    internal static ScriptExecutionGrant RequestAccess(
        Guid id,
        Guid vaultId,
        Guid organizationId,
        Guid agentId,
        string agentPublicKey,
        Guid scriptEntryId,
        GrantNames names,
        EncryptedReasonEnvelope encryptedReason,
        Instant now,
        uint agentAccessEpoch)
    {
        ArgumentOutOfRangeException.ThrowIfZero(agentAccessEpoch);
        var grant = new ScriptExecutionGrant
        {
            Id = id,
            VaultId = vaultId,
            OrganizationId = organizationId,
            AgentId = agentId,
            AgentAccessEpoch = agentAccessEpoch,
            AgentPublicKey = agentPublicKey,
            ScriptEntryId = scriptEntryId,
            Status = GrantStatus.Pending,
            RequestType = GrantRequestType.AccessRequest,
            EncryptedReason = encryptedReason,
            Methods = GrantMethods.Exec,
            QueryCount = 0,
            CreatedAt = now,
            CreatedBy = null,
            UpdatedAt = now,
        };

        grant.EmitRequested(scriptEntryId, names);
        return grant;
    }

    internal static ScriptExecutionGrant CreateProactively(
        Guid id,
        Guid vaultId,
        Guid organizationId,
        Guid agentId,
        string agentPublicKey,
        Guid scriptEntryId,
        IReadOnlyCollection<ScriptExecutionScope> scopes,
        ScriptExecutionPackage package,
        Instant? expiresAt,
        int? queryLimit,
        string expirySource,
        Guid createdBy,
        GrantNames names,
        Instant now,
        uint agentAccessEpoch)
    {
        ArgumentOutOfRangeException.ThrowIfZero(agentAccessEpoch);
        if (package.OrganizationId != organizationId || package.VaultId != vaultId
            || package.GrantId != id || package.AgentId != agentId
            || package.AgentAccessEpoch != agentAccessEpoch || package.ScriptEntryId != scriptEntryId
            || package.PackageRevision != 1
            || scopes.Count is < 1 or > 65
            || scopes.Any(scope => scope.OrganizationId != organizationId || scope.VaultId != vaultId
                || scope.GrantId != id)
            || scopes.Select(scope => scope.EntryId).Distinct().Count() != scopes.Count
            || scopes.Count(scope => scope.IsScript) != 1
            || scopes.Single(scope => scope.IsScript).EntryId != scriptEntryId
            || scopes.Single(scope => scope.IsScript).EntryRevision != package.ScriptRevision)
        {
            throw new DomainException("Script execution package does not match the grant.");
        }

        var grant = new ScriptExecutionGrant
        {
            Id = id,
            VaultId = vaultId,
            OrganizationId = organizationId,
            AgentId = agentId,
            AgentAccessEpoch = agentAccessEpoch,
            AgentPublicKey = agentPublicKey,
            ScriptEntryId = scriptEntryId,
            Status = GrantStatus.Active,
            ExpiresAt = expiresAt,
            QueryLimit = queryLimit,
            QueryCount = 0,
            ExpirySource = expirySource,
            Methods = GrantMethods.Exec,
            CreatedAt = now,
            CreatedBy = createdBy,
            UpdatedAt = now,
            ScriptExecutionScopes = scopes.ToList(),
            ScriptExecutionPackage = package,
        };

        grant.EmitCreated(names);
        return grant;
    }

    internal void RefreshPackage(
        ScriptExecutionPackage replacement,
        IReadOnlyCollection<ScriptExecutionScope> replacementScopes)
    {
        if (Status != GrantStatus.Active || ScriptExecutionPackage is null
            || replacementScopes.Count is < 1 or > 65
            || replacementScopes.Any(scope => scope.OrganizationId != OrganizationId
                || scope.VaultId != VaultId || scope.GrantId != Id)
            || replacementScopes.Select(scope => scope.EntryId).Distinct().Count() != replacementScopes.Count
            || replacementScopes.Count(scope => scope.IsScript) != 1
            || replacementScopes.Single(scope => scope.IsScript).EntryId != ScriptEntryId
            || replacementScopes.Single(scope => scope.IsScript).EntryRevision != replacement.ScriptRevision)
        {
            throw new DomainException("Script execution package refresh is invalid.");
        }

        ScriptExecutionPackage.RefreshFrom(replacement);
        foreach (var existing in ScriptExecutionScopes.ToArray())
        {
            var next = replacementScopes.SingleOrDefault(scope => scope.EntryId == existing.EntryId);
            if (next is null)
            {
                ScriptExecutionScopes.Remove(existing);
                continue;
            }
            existing.RefreshFrom(next);
        }
        foreach (var next in replacementScopes
                     .Where(next => ScriptExecutionScopes.All(existing => existing.EntryId != next.EntryId)))
        {
            ScriptExecutionScopes.Add(next);
        }
    }

    internal void Approve(
        Guid approvedBy,
        GrantNames names,
        ScriptExecutionPackage package,
        IReadOnlyCollection<ScriptExecutionScope> scopes,
        Instant? expiresAt,
        int? queryLimit,
        string expirySource,
        Instant now)
    {
        EnsurePendingApproval();
        if (!PackageMatchesGrant(
                package,
                scopes,
                OrganizationId,
                VaultId,
                Id,
                AgentId,
                AgentAccessEpoch,
                ScriptEntryId))
        {
            throw new DomainException("Script execution package does not match the pending grant.");
        }

        ScriptExecutionPackage = package;
        ScriptExecutionScopes = scopes.ToList();
        CompleteApproval(
            approvedBy,
            names,
            expiresAt,
            queryLimit,
            expirySource,
            GrantMethods.Exec,
            now);
    }

    private static bool PackageMatchesGrant(
        ScriptExecutionPackage package,
        IReadOnlyCollection<ScriptExecutionScope> scopes,
        Guid organizationId,
        Guid vaultId,
        Guid grantId,
        Guid agentId,
        uint agentAccessEpoch,
        Guid scriptEntryId) =>
        package.OrganizationId == organizationId
        && package.VaultId == vaultId
        && package.GrantId == grantId
        && package.AgentId == agentId
        && package.AgentAccessEpoch == agentAccessEpoch
        && package.ScriptEntryId == scriptEntryId
        && package.PackageRevision == 1
        && scopes.Count is >= 1 and <= 65
        && scopes.All(scope => scope.OrganizationId == organizationId
                               && scope.VaultId == vaultId
                               && scope.GrantId == grantId)
        && scopes.Select(scope => scope.EntryId).Distinct().Count() == scopes.Count
        && scopes.Count(scope => scope.IsScript) == 1
        && scopes.Single(scope => scope.IsScript).EntryId == scriptEntryId
        && scopes.Single(scope => scope.IsScript).EntryRevision == package.ScriptRevision;
}
