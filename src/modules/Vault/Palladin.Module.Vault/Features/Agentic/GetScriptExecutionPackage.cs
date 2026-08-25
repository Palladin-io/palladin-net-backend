using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;
using Palladin.Core.Events;
using Palladin.Core.Types;
using Palladin.Module.Agents.Infrastructure.AgentAuth;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record GetScriptExecutionPackageRequest
{
    public Guid VaultId { get; init; }
    public Guid ScriptEntryId { get; init; }
    public string ScriptRevision { get; init; } = string.Empty;
}

[PublicAPI]
public sealed record ScriptExecutionVaultEntryContract(
    Guid EntryId,
    string EntryRevision,
    GrantDeliveryPolicy DeliveryPolicy,
    VaultEntryKeyContract EntryKey,
    MemberSecretEnvelopeContract MemberSecret);

[PublicAPI]
public sealed record GetScriptExecutionPackageResponse(
    string Status,
    string AuthorizationSource,
    Guid OrganizationId,
    Guid VaultId,
    Guid AgentId,
    uint AgentAccessEpoch,
    Guid ScriptEntryId,
    string ScriptRevision,
    Guid GrantId,
    int QueryCount,
    int? QueryLimit,
    Instant? ExpiresAt,
    ScriptExecutionPackageContract? ScriptPackage,
    AgentWrappedVaultKeyContract? AgentWrappedVaultKey,
    IReadOnlyList<ScriptExecutionVaultEntryContract>? VaultEntries);

[UsedImplicitly]
internal sealed class GetScriptExecutionPackageValidator
    : Validator<GetScriptExecutionPackageRequest>
{
    public GetScriptExecutionPackageValidator()
    {
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.ScriptEntryId).NotEmpty();
        RuleFor(x => x.ScriptRevision)
            .NotEmpty()
            .Must(value =>
            {
                try
                {
                    return VaultEnvelopeContractMapper.ParseUInt64(value) > 0;
                }
                catch (Palladin.Core.Types.Exceptions.DomainException)
                {
                    return false;
                }
            });
    }
}

internal static class ScriptPackageDenialReasons
{
    internal const string NoActiveGrant = "not-granted";
    internal const string MethodNotAllowed = "method-not-allowed";
    internal const string OverlappingGrants = "overlapping-grants";
    internal const string Expired = "expired";
    internal const string QueryLimit = "grant-exhausted";
    internal const string MaterialUnavailable = "invalid-package";
    internal const string StaleScript = "stale-discovery";
}

internal abstract record ScriptPackageDeliveryResult
{
    internal sealed record Granted(
        string AuthorizationSource,
        string AgentName,
        Guid GrantId,
        int QueryCount,
        int? QueryLimit,
        Instant? ExpiresAt,
        ScriptExecutionPackageContract? ScriptPackage,
        AgentWrappedVaultKeyContract? AgentWrappedVaultKey,
        IReadOnlyList<ScriptExecutionVaultEntryContract>? VaultEntries,
        bool Consumed) : ScriptPackageDeliveryResult;

    internal sealed record Denied(string Reason) : ScriptPackageDeliveryResult;
}

[UsedImplicitly]
internal sealed class ScriptExecutionPackageDeliveryService(
    VaultDomainReadContext domainReadContext,
    VaultDomainWriteContext domainWriteContext)
{
    private const int MaximumFullPackageCiphertextBytes = 2_097_152;

    internal async Task<ScriptPackageDeliveryResult> ExecuteAsync(
        Guid organizationId,
        Guid agentId,
        uint agentAccessEpoch,
        Guid vaultId,
        Guid scriptEntryId,
        ulong scriptRevision,
        string? ip,
        string? hostname,
        Instant now,
        CancellationToken ct)
    {
        var agent = await domainReadContext.Agents
            .Where(candidate => candidate.OrganizationId == organizationId
                && candidate.Id == agentId
                && candidate.Status == AgentStatus.Active
                && candidate.AccessEpoch == agentAccessEpoch)
            .Select(candidate => new
            {
                candidate.Name,
                candidate.RecipientKeyVersion,
                candidate.PublicKey,
            })
            .SingleOrDefaultAsync(ct);
        if (agent is null)
        {
            return new ScriptPackageDeliveryResult.Denied(ScriptPackageDenialReasons.NoActiveGrant);
        }

        var directGrants = await domainReadContext.Grants
            .OfType<ScriptExecutionGrant>()
            .Where(grant => grant.OrganizationId == organizationId
                && grant.AgentId == agentId
                && grant.AgentAccessEpoch == agentAccessEpoch
                && grant.VaultId == vaultId
                && grant.ScriptEntryId == scriptEntryId
                && grant.Status == GrantStatus.Active)
            .Select(grant => new
            {
                grant.Id,
                grant.ExpiresAt,
                grant.QueryLimit,
                grant.QueryCount,
                grant.Methods,
            })
            .ToListAsync(ct);
        var fullGrantCandidates = await domainReadContext.Grants
            .OfType<FullGrant>()
            .Where(grant => grant.OrganizationId == organizationId
                && grant.AgentId == agentId
                && grant.AgentAccessEpoch == agentAccessEpoch
                && grant.VaultId == vaultId
                && grant.Status == GrantStatus.Active)
            .Select(grant => new
            {
                grant.Id,
                grant.ExpiresAt,
                grant.QueryLimit,
                grant.QueryCount,
                grant.Methods,
            })
            .ToListAsync(ct);
        var fullGrants = fullGrantCandidates
            .Where(grant => (grant.Methods & GrantMethods.Exec) == GrantMethods.Exec)
            .ToList();

        if (directGrants.Count > 0 && fullGrants.Count > 0
            || directGrants.Count > 1 || fullGrants.Count > 1)
        {
            return new ScriptPackageDeliveryResult.Denied(ScriptPackageDenialReasons.OverlappingGrants);
        }
        if (directGrants.Count == 0 && fullGrants.Count == 0)
        {
            if (fullGrantCandidates.Count > 0)
            {
                return new ScriptPackageDeliveryResult.Denied(
                    ScriptPackageDenialReasons.MethodNotAllowed);
            }

            var exhaustedGrantExists = await domainReadContext.Grants.AnyAsync(grant =>
                grant.OrganizationId == organizationId
                && grant.AgentId == agentId
                && grant.AgentAccessEpoch == agentAccessEpoch
                && grant.VaultId == vaultId
                && grant.Status == GrantStatus.Consumed
                && grant.QueryLimit != null
                && grant.QueryCount >= grant.QueryLimit
                && ((grant is ScriptExecutionGrant
                        && ((ScriptExecutionGrant)grant).ScriptEntryId == scriptEntryId)
                    || (grant is FullGrant
                        && (grant.Methods & GrantMethods.Exec) == GrantMethods.Exec)), ct);
            return new ScriptPackageDeliveryResult.Denied(exhaustedGrantExists
                ? ScriptPackageDenialReasons.QueryLimit
                : ScriptPackageDenialReasons.NoActiveGrant);
        }

        var selected = directGrants.Count == 1 ? directGrants[0] : fullGrants[0];
        if (selected.ExpiresAt is not null && selected.ExpiresAt <= now)
        {
            return new ScriptPackageDeliveryResult.Denied(ScriptPackageDenialReasons.Expired);
        }
        if (selected.QueryLimit is not null && selected.QueryCount >= selected.QueryLimit)
        {
            return new ScriptPackageDeliveryResult.Denied(ScriptPackageDenialReasons.QueryLimit);
        }

        var script = await domainReadContext.Entries
            .Where(entry => entry.OrganizationId == organizationId
                && entry.VaultId == vaultId
                && entry.Id == scriptEntryId
                && entry.State == EntryState.Active)
            .Select(entry => new { entry.CurrentRevision, entry.DeliveryPolicy })
            .SingleOrDefaultAsync(ct);
        if (script is null || script.DeliveryPolicy != GrantDeliveryPolicy.ExecOnly)
        {
            return new ScriptPackageDeliveryResult.Denied(ScriptPackageDenialReasons.MaterialUnavailable);
        }
        if (script.CurrentRevision.Value != scriptRevision)
        {
            return new ScriptPackageDeliveryResult.Denied(ScriptPackageDenialReasons.StaleScript);
        }

        ScriptExecutionPackageContract? directPackage = null;
        AgentWrappedVaultKeyContract? wrappedVaultKey = null;
        IReadOnlyList<ScriptExecutionVaultEntryContract>? fullEntries = null;
        Guid[] snapshotEntryIds;
        decimal[] snapshotEntryRevisions;
        decimal snapshotPackageRevision = 0;
        decimal snapshotVaultKeyVersion = 0;
        decimal snapshotRecipientKeyVersion;
        var authorizationSource = directGrants.Count == 1 ? "scriptExecution" : "full";

        if (directGrants.Count == 1)
        {
            var vaultTrustAnchor = await domainReadContext.Vaults
                .Where(value => value.OrganizationId == organizationId && value.Id == vaultId)
                .Select(value => new
                {
                    SigningKeyVersion = value.CurrentManifestSigningKeyVersion.Value,
                    SigningKeyFingerprint = value.ManifestSigningKeyFingerprint,
                })
                .SingleOrDefaultAsync(ct);
            var package = await domainReadContext.ScriptExecutionPackages
                .SingleOrDefaultAsync(value => value.OrganizationId == organizationId
                    && value.VaultId == vaultId
                    && value.GrantId == selected.Id
                    && value.AgentId == agentId
                    && value.AgentAccessEpoch == agentAccessEpoch
                    && value.ScriptEntryId == scriptEntryId, ct);
            var scopes = await domainReadContext.ScriptExecutionScopes
                .Where(scope => scope.OrganizationId == organizationId
                    && scope.VaultId == vaultId
                    && scope.GrantId == selected.Id)
                .ToListAsync(ct);
            if (package is null || vaultTrustAnchor is null || package.ScriptRevision != scriptRevision
                || package.RecipientAgentKeyVersion != agent.RecipientKeyVersion
                || package.VaultSigningKeyVersion != vaultTrustAnchor.SigningKeyVersion
                || !package.VaultSigningKeyFingerprint.AsSpan()
                    .SequenceEqual(vaultTrustAnchor.SigningKeyFingerprint)
                || scopes.Count is < 1 or > 65
                || scopes.Count(scope => scope.IsScript) != 1
                || scopes.Single(scope => scope.IsScript).EntryId != scriptEntryId)
            {
                return new ScriptPackageDeliveryResult.Denied(ScriptPackageDenialReasons.MaterialUnavailable);
            }

            var fingerprint = VaultKeyFingerprint.Compute(
                Convert.FromBase64String(agent.PublicKey), VaultKeyKind.AgentX25519);
            if (!package.RecipientAgentKeyFingerprint.AsSpan().SequenceEqual(fingerprint))
            {
                return new ScriptPackageDeliveryResult.Denied(ScriptPackageDenialReasons.MaterialUnavailable);
            }

            var scopeIds = scopes.Select(scope => scope.EntryId).ToArray();
            var currentScopes = await domainReadContext.Entries
                .Where(entry => entry.OrganizationId == organizationId
                    && entry.VaultId == vaultId
                    && entry.State == EntryState.Active
                    && scopeIds.Contains(entry.Id))
                .Select(entry => new { entry.Id, Revision = entry.CurrentRevision.Value })
                .ToListAsync(ct);
            if (currentScopes.Count != scopes.Count
                || scopes.Any(scope => currentScopes.All(current => current.Id != scope.EntryId
                    || current.Revision != scope.EntryRevision)))
            {
                return new ScriptPackageDeliveryResult.Denied(ScriptPackageDenialReasons.MaterialUnavailable);
            }

            directPackage = ScriptExecutionPackageContractMapper.ToContract(package, scopes);
            snapshotEntryIds = scopes.Select(scope => scope.EntryId).ToArray();
            snapshotEntryRevisions = scopes.Select(scope => (decimal)scope.EntryRevision).ToArray();
            snapshotPackageRevision = package.PackageRevision;
            snapshotRecipientKeyVersion = package.RecipientAgentKeyVersion;
        }
        else
        {
            var wrapped = await domainReadContext.AgentWrappedVaultKeys
                .SingleOrDefaultAsync(value => value.OrganizationId == organizationId
                    && value.VaultId == vaultId
                    && value.GrantId == selected.Id
                    && value.AgentId == agentId
                    && value.AgentAccessEpoch == agentAccessEpoch, ct);
            var fingerprint = VaultKeyFingerprint.Compute(
                Convert.FromBase64String(agent.PublicKey), VaultKeyKind.AgentX25519);
            if (wrapped is null
                || wrapped.RecipientAgentKeyVersion.Value != agent.RecipientKeyVersion
                || !wrapped.RecipientAgentKeyFingerprint.AsSpan().SequenceEqual(fingerprint))
            {
                return new ScriptPackageDeliveryResult.Denied(ScriptPackageDenialReasons.MaterialUnavailable);
            }

            var entries = await domainReadContext.Entries
                .Where(entry => entry.OrganizationId == organizationId
                    && entry.VaultId == vaultId
                    && entry.State == EntryState.Active)
                .Select(entry => new
                {
                    entry.Id,
                    Revision = entry.CurrentRevision.Value,
                    KeyVersion = entry.CurrentKeyVersion.Value,
                    entry.DeliveryPolicy,
                })
                .OrderBy(entry => entry.Id)
                .ToListAsync(ct);
            var entryIds = entries.Select(entry => entry.Id).ToArray();
            var keys = await (
                from key in domainReadContext.EntryKeys
                join entry in domainReadContext.Entries
                    on new { key.OrganizationId, key.VaultId, key.EntryId, key.KeyVersion }
                    equals new
                    {
                        entry.OrganizationId,
                        entry.VaultId,
                        EntryId = entry.Id,
                        KeyVersion = entry.CurrentKeyVersion,
                    }
                where entry.OrganizationId == organizationId
                    && entry.VaultId == vaultId
                    && entry.State == EntryState.Active
                    && entryIds.Contains(entry.Id)
                select key)
                .ToListAsync(ct);
            var versions = await (
                from version in domainReadContext.EntryVersions
                join entry in domainReadContext.Entries
                    on new { version.OrganizationId, version.VaultId, version.EntryId, version.Revision }
                    equals new
                    {
                        entry.OrganizationId,
                        entry.VaultId,
                        EntryId = entry.Id,
                        Revision = entry.CurrentRevision,
                    }
                where entry.OrganizationId == organizationId
                    && entry.VaultId == vaultId
                    && entry.State == EntryState.Active
                    && entryIds.Contains(entry.Id)
                select version)
                .ToListAsync(ct);

            var result = new List<ScriptExecutionVaultEntryContract>(entries.Count);
            var totalCiphertextBytes = 0;
            foreach (var entry in entries)
            {
                var key = keys.SingleOrDefault(value => value.EntryId == entry.Id
                    && value.KeyVersion.Value == entry.KeyVersion);
                var version = versions.SingleOrDefault(value => value.EntryId == entry.Id
                    && value.Revision.Value == entry.Revision);
                if (key is null || version is null || key.WrappingKeyVersion != wrapped.VaultKeyVersion)
                {
                    return new ScriptPackageDeliveryResult.Denied(ScriptPackageDenialReasons.MaterialUnavailable);
                }

                totalCiphertextBytes = checked(totalCiphertextBytes
                    + key.EncodedSuitePayload.Length
                    + version.MemberSecretEncodedSuitePayload.Length);
                if (totalCiphertextBytes > MaximumFullPackageCiphertextBytes)
                {
                    return new ScriptPackageDeliveryResult.Denied(ScriptPackageDenialReasons.MaterialUnavailable);
                }
                result.Add(new ScriptExecutionVaultEntryContract(
                    entry.Id,
                    entry.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    entry.DeliveryPolicy,
                    VaultEnvelopeContractMapper.ToContract(key),
                    VaultEnvelopeContractMapper.ToContract(version.GetMemberSecret())));
            }

            wrappedVaultKey = AgentWrappedVaultKeyContractMapper.ToContract(wrapped);
            fullEntries = result;
            snapshotEntryIds = entries.Select(entry => entry.Id).ToArray();
            snapshotEntryRevisions = entries.Select(entry => (decimal)entry.Revision).ToArray();
            snapshotVaultKeyVersion = wrapped.VaultKeyVersion.Value;
            snapshotRecipientKeyVersion = wrapped.RecipientAgentKeyVersion.Value;
        }

        // APPROVED PERSISTENCE DEVIATION: the grant guard, revision snapshot validation,
        // check-and-increment, terminal transition and key/package deletion must be one PostgreSQL
        // statement. A tracked aggregate read followed by CommitAsync cannot guarantee that exactly
        // one concurrent caller receives the final allowed package. The SQL is tenant-scoped and is
        // exposed only through VaultDomainWriteContext.
        var newCount = await IncrementUseAsync(
            selected.Id,
            authorizationSource,
            organizationId,
            agentId,
            agentAccessEpoch,
            vaultId,
            scriptEntryId,
            scriptRevision,
            snapshotEntryIds,
            snapshotEntryRevisions,
            snapshotPackageRevision,
            snapshotVaultKeyVersion,
            snapshotRecipientKeyVersion,
            ip,
            hostname,
            now,
            ct);
        if (newCount is null)
        {
            var stillActive = await domainReadContext.Grants.AnyAsync(grant => grant.Id == selected.Id
                && grant.OrganizationId == organizationId
                && grant.AgentId == agentId
                && grant.AgentAccessEpoch == agentAccessEpoch
                && grant.VaultId == vaultId
                && grant.Status == GrantStatus.Active, ct);
            return new ScriptPackageDeliveryResult.Denied(stillActive
                ? ScriptPackageDenialReasons.MaterialUnavailable
                : ScriptPackageDenialReasons.QueryLimit);
        }

        return new ScriptPackageDeliveryResult.Granted(
            authorizationSource,
            agent.Name ?? CredentialAccessedEvent.UnknownAgent,
            selected.Id,
            newCount.Value,
            selected.QueryLimit,
            selected.ExpiresAt,
            directPackage,
            wrappedVaultKey,
            fullEntries,
            selected.QueryLimit is not null && newCount >= selected.QueryLimit);
    }

    private async Task<int?> IncrementUseAsync(
        Guid grantId,
        string authorizationSource,
        Guid organizationId,
        Guid agentId,
        uint agentAccessEpoch,
        Guid vaultId,
        Guid scriptEntryId,
        ulong scriptRevision,
        Guid[] snapshotEntryIds,
        decimal[] snapshotEntryRevisions,
        decimal snapshotPackageRevision,
        decimal snapshotVaultKeyVersion,
        decimal snapshotRecipientKeyVersion,
        string? ip,
        string? hostname,
        Instant now,
        CancellationToken ct)
    {
        var expectedGrantType = authorizationSource == "full"
            ? GrantType.Full.ToString()
            : GrantType.ScriptExecution.ToString();
        var rows = await domainWriteContext.SqlQuery<int>(
            $"""
             WITH snapshot AS MATERIALIZED (
                 SELECT requested."EntryId", requested."EntryRevision"
                 FROM unnest({snapshotEntryIds}, {snapshotEntryRevisions})
                      AS requested("EntryId", "EntryRevision")
             ), updated AS (
                 UPDATE "Grants" AS selected_grant
                 SET "QueryCount" = selected_grant."QueryCount" + 1,
                     "Status" = CASE
                         WHEN selected_grant."QueryLimit" IS NOT NULL
                              AND selected_grant."QueryCount" + 1 >= selected_grant."QueryLimit"
                         THEN {(int)GrantStatus.Consumed}
                         ELSE selected_grant."Status"
                     END,
                     "UpdatedAt" = {now},
                     "LastAccessedAt" = {now},
                     "LastAccessIp" = {ip},
                     "LastAccessHostname" = {hostname}
                 WHERE selected_grant."Id" = {grantId}
                   AND selected_grant."OrganizationId" = {organizationId}
                   AND selected_grant."AgentId" = {agentId}
                   AND selected_grant."AgentAccessEpoch" = {agentAccessEpoch}
                   AND selected_grant."VaultId" = {vaultId}
                   AND selected_grant."GrantType" = {expectedGrantType}
                   AND selected_grant."Status" = {(int)GrantStatus.Active}
                   AND (selected_grant."Methods" & {(int)GrantMethods.Exec}) = {(int)GrantMethods.Exec}
                   AND (selected_grant."ExpiresAt" IS NULL OR selected_grant."ExpiresAt" > {now})
                   AND (selected_grant."QueryLimit" IS NULL OR selected_grant."QueryCount" < selected_grant."QueryLimit")
                   AND selected_grant."QueryCount" < {int.MaxValue}
                   AND EXISTS (
                       SELECT 1 FROM "Agents" AS agent
                       WHERE agent."OrganizationId" = {organizationId}
                         AND agent."Id" = {agentId}
                         AND agent."Status" = {(int)AgentStatus.Active}
                         AND agent."AccessEpoch" = {agentAccessEpoch})
                   AND EXISTS (
                       SELECT 1 FROM "VaultEntries" AS script
                       WHERE script."OrganizationId" = {organizationId}
                         AND script."VaultId" = {vaultId}
                         AND script."Id" = {scriptEntryId}
                         AND script."State" = {(int)EntryState.Active}
                         AND script."DeliveryPolicy" = {(int)GrantDeliveryPolicy.ExecOnly}
                         AND script."CurrentRevision" = {(decimal)scriptRevision})
                   AND NOT EXISTS (
                       SELECT 1
                       FROM snapshot
                       LEFT JOIN "VaultEntries" AS entry
                         ON entry."OrganizationId" = {organizationId}
                        AND entry."VaultId" = {vaultId}
                        AND entry."Id" = snapshot."EntryId"
                        AND entry."State" = {(int)EntryState.Active}
                       WHERE entry."Id" IS NULL
                          OR entry."CurrentRevision" <> snapshot."EntryRevision")
                   AND (
                       {authorizationSource} <> 'full'
                       OR NOT EXISTS (
                           SELECT 1
                           FROM "VaultEntries" AS active_entry
                           WHERE active_entry."OrganizationId" = {organizationId}
                             AND active_entry."VaultId" = {vaultId}
                             AND active_entry."State" = {(int)EntryState.Active}
                             AND NOT EXISTS (
                                 SELECT 1
                                 FROM snapshot
                                 WHERE snapshot."EntryId" = active_entry."Id")))
                   AND (
                       ({authorizationSource} = 'scriptExecution'
                        AND EXISTS (
                            SELECT 1 FROM "ScriptExecutionPackages" AS package
                            WHERE package."OrganizationId" = {organizationId}
                              AND package."VaultId" = {vaultId}
                              AND package."GrantId" = {grantId}
                              AND package."AgentId" = {agentId}
                              AND package."AgentAccessEpoch" = {agentAccessEpoch}
                              AND package."ScriptEntryId" = {scriptEntryId}
                              AND package."ScriptRevision" = {(decimal)scriptRevision}
                              AND package."PackageRevision" = {snapshotPackageRevision}
                              AND package."RecipientAgentKeyVersion" = {snapshotRecipientKeyVersion})
                        AND NOT EXISTS (
                            SELECT 1 FROM "Grants" AS overlap
                            WHERE overlap."OrganizationId" = {organizationId}
                              AND overlap."VaultId" = {vaultId}
                              AND overlap."AgentId" = {agentId}
                              AND overlap."AgentAccessEpoch" = {agentAccessEpoch}
                              AND overlap."GrantType" = {GrantType.Full.ToString()}
                              AND overlap."Status" = {(int)GrantStatus.Active}
                              AND (overlap."Methods" & {(int)GrantMethods.Exec}) = {(int)GrantMethods.Exec}))
                       OR
                       ({authorizationSource} = 'full'
                        AND EXISTS (
                            SELECT 1 FROM "AgentWrappedVaultKeys" AS wrapped
                            WHERE wrapped."OrganizationId" = {organizationId}
                              AND wrapped."VaultId" = {vaultId}
                              AND wrapped."GrantId" = {grantId}
                              AND wrapped."AgentId" = {agentId}
                              AND wrapped."AgentAccessEpoch" = {agentAccessEpoch}
                              AND wrapped."VaultKeyVersion" = {snapshotVaultKeyVersion}
                              AND wrapped."RecipientAgentKeyVersion" = {snapshotRecipientKeyVersion})
                        AND NOT EXISTS (
                            SELECT 1
                            FROM "Grants" AS overlap
                            WHERE overlap."OrganizationId" = {organizationId}
                              AND overlap."VaultId" = {vaultId}
                              AND overlap."AgentId" = {agentId}
                              AND overlap."AgentAccessEpoch" = {agentAccessEpoch}
                              AND overlap."GrantType" = {GrantType.ScriptExecution.ToString()}
                              AND overlap."ScriptEntryId" = {scriptEntryId}
                              AND overlap."Status" = {(int)GrantStatus.Active}))
                   )
                 RETURNING selected_grant."QueryCount", selected_grant."Status"
             ), deleted_direct AS (
                 DELETE FROM "ScriptExecutionPackages"
                 WHERE "OrganizationId" = {organizationId}
                   AND "VaultId" = {vaultId}
                   AND "GrantId" = {grantId}
                   AND {authorizationSource} = 'scriptExecution'
                   AND EXISTS (SELECT 1 FROM updated WHERE "Status" = {(int)GrantStatus.Consumed})
             ), deleted_full AS (
                 DELETE FROM "AgentWrappedVaultKeys"
                 WHERE "OrganizationId" = {organizationId}
                   AND "VaultId" = {vaultId}
                   AND "GrantId" = {grantId}
                   AND {authorizationSource} = 'full'
                   AND EXISTS (SELECT 1 FROM updated WHERE "Status" = {(int)GrantStatus.Consumed})
             )
             SELECT "QueryCount" AS "Value" FROM updated
             """)
            .ToListAsync(ct);
        return rows.Count == 1 ? rows[0] : null;
    }
}

[PublicAPI]
internal sealed class GetScriptExecutionPackageEndpoint(
    ScriptExecutionPackageDeliveryService deliveryService,
    IEnumerable<IEventPublisher> eventPublishers,
    ILogger<GetScriptExecutionPackageEndpoint> logger,
    IClock clock) : Endpoint<GetScriptExecutionPackageRequest, GetScriptExecutionPackageResponse>
{
    public override void Configure()
    {
        Post("api/agent/vaults/{vaultId:guid}/scripts/{scriptEntryId:guid}/execution-package");
        AuthSchemes(AgentAuthenticationOptions.SchemeName);
        Summary(summary =>
        {
            summary.Summary = "Deliver one encrypted Script execution package";
            summary.Description = "Uses exactly one direct ScriptExecution grant or one covering FULL Exec grant. Parameters and results never cross this endpoint; the backend relays only ciphertext and structural revisions.";
        });
        Tags("Vault/Grants");
    }

    public override async Task HandleAsync(GetScriptExecutionPackageRequest req, CancellationToken ct)
    {
        var agentId = User.GetAgentId();
        var organizationId = User.GetAgentOrganizationId();
        var accessEpoch = User.GetAgentAccessEpoch();
        if (agentId is null || organizationId is null || accessEpoch is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var now = clock.GetCurrentInstant();
        var result = await deliveryService.ExecuteAsync(
            organizationId.Value,
            agentId.Value,
            accessEpoch.Value,
            req.VaultId,
            req.ScriptEntryId,
            VaultEnvelopeContractMapper.ParseUInt64(req.ScriptRevision),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            HttpContext.Request.Headers[AgentAuthenticationOptions.AgentHostnameHeader].FirstOrDefault(),
            now,
            ct);

        if (result is ScriptPackageDeliveryResult.Denied denied)
        {
            AddError(denied.Reason);
            await Send.ErrorsAsync(denied.Reason switch
            {
                ScriptPackageDenialReasons.NoActiveGrant => 404,
                ScriptPackageDenialReasons.MethodNotAllowed => 403,
                ScriptPackageDenialReasons.QueryLimit => 429,
                ScriptPackageDenialReasons.Expired => 403,
                _ => 409,
            }, ct);
            return;
        }

        var granted = (ScriptPackageDeliveryResult.Granted)result;
        await Send.OkAsync(new GetScriptExecutionPackageResponse(
            "granted",
            granted.AuthorizationSource,
            organizationId.Value,
            req.VaultId,
            agentId.Value,
            accessEpoch.Value,
            req.ScriptEntryId,
            req.ScriptRevision,
            granted.GrantId,
            granted.QueryCount,
            granted.QueryLimit,
            granted.ExpiresAt,
            granted.ScriptPackage,
            granted.AgentWrappedVaultKey,
            granted.VaultEntries), ct);

        await PublishPostResponseAsync(new CredentialAccessedEvent(
            granted.GrantId,
            req.VaultId,
            organizationId.Value,
            agentId.Value,
            req.ScriptEntryId,
            granted.AuthorizationSource == "full" ? GrantType.Full : GrantType.ScriptExecution,
            granted.AgentName,
            string.Empty,
            string.Empty,
            granted.ExpiresAt,
            granted.QueryLimit is null ? null : granted.QueryLimit - granted.QueryCount,
            GrantMethods.Exec,
            now), ct);

        if (granted.Consumed)
        {
            await PublishPostResponseAsync(new GrantConsumedEvent(
                granted.GrantId,
                req.VaultId,
                organizationId.Value,
                agentId.Value,
                granted.AuthorizationSource == "scriptExecution" ? req.ScriptEntryId : null,
                null,
                now), ct);
        }
    }

    private async Task PublishPostResponseAsync<TEvent>(TEvent @event, CancellationToken ct)
        where TEvent : Palladin.Core.Events.IEvent
    {
        try
        {
            foreach (var publisher in eventPublishers)
            {
                await publisher.PublishAsync(@event, ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Post-response publish of {EventType} failed", typeof(TEvent).Name);
        }
    }
}
