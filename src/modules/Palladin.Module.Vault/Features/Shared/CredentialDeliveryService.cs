using Palladin.Core.Types;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Infrastructure.Persistence;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Palladin.Module.Vault.Features;

// Machine-readable denial reasons emitted on CredentialAccessDeniedEvent (audit + analytics). Stable
// strings — clients/triggers branch on them. Shared by DeliverCredential and GetOrRequestCredential
// so both delivery entry points produce the same audit trail.
internal static class CredentialDenialReasons
{
    public const string NoActiveGrant = "no_active_grant";
    public const string Expired = "expired";
    public const string MaterialUnavailable = "material_unavailable";
    public const string QueryLimit = "query_limit";
    public const string MethodNotAllowed = "method_not_allowed";
    public const string ScriptExecOnly = "script_exec_only";
}

internal sealed record CredentialDeliveryInput(
    Guid GrantId,
    Guid OrganizationId,
    Guid AgentId,
    Guid VaultId,
    Guid EntryId,
    uint AgentAccessEpoch,
    Instant? ExpiresAt,
    int? QueryLimit,
    GrantType Type,
    GrantMethods AllowedMethods,
    GrantMethods Method,
    string? Ip,
    string? Hostname,
    Instant Now);

internal abstract record CredentialDeliveryResult
{
    public sealed record Granted(
        ulong GrantEnvelopeRevision,
        ulong EntryRevision,
        ushort ProtocolVersion,
        string CryptoSuiteId,
        uint GrantKeyVersion,
        uint MemberKeyGeneration,
        uint RecipientAgentKeyVersion,
        string[] FieldIds,
        byte[] EncodedSuitePayload,
        byte[] AgentWrappedGrantDek,
        string WrapperSuiteId,
        byte[] AgentKeyFingerprint,
        Instant? EnvelopeExpiresAt,
        int? EnvelopeRemainingUses,
        int? RemainingUses,
        bool Consumed,
        string AgentName,
        string VaultName) : CredentialDeliveryResult;

    public sealed record Denied(string Reason) : CredentialDeliveryResult;
}

// Shared delivery path used by DeliverCredentialEndpoint (legacy GET) and
// GetOrRequestCredentialEndpoint (unified POST). Runs the expiry guard, reads the per-agent crypto
// material, performs the atomic race-safe check-and-increment / Consumed flip, and resolves the
// denormalized names carried on CredentialAccessedEvent. Returns a discriminated result so the
// caller owns the HTTP shape and the post-response publish.
[UsedImplicitly]
internal sealed class CredentialDeliveryService(
    VaultDomainReadContext domainReadContext,
    VaultDomainWriteContext domainWriteContext)
{
    public async Task<CredentialDeliveryResult> ExecuteAsync(
        CredentialDeliveryInput input,
        CancellationToken ct)
    {
        if (input.ExpiresAt is not null && input.ExpiresAt.Value <= input.Now)
        {
            return new CredentialDeliveryResult.Denied(CredentialDenialReasons.Expired);
        }

        // The grant whitelists how the CLI may use the credential through bitwise method flags
        // flags chosen by the approving user). Checked before any material is read so a disallowed
        // method never burns a use.
        if (!input.AllowedMethods.HasFlag(input.Method))
        {
            return new CredentialDeliveryResult.Denied(CredentialDenialReasons.MethodNotAllowed);
        }

        // Validate the complete tenant scope before reading grant material or incrementing a use.
        // Entry metadata is encrypted client-side and is never interpreted by this service.
        var activeEntry = await domainReadContext.Entries
            .Where(e => e.OrganizationId == input.OrganizationId
                        && e.VaultId == input.VaultId
                        && e.Id == input.EntryId
                        && e.State == EntryState.Active)
            .Select(e => new { e.CurrentRevision })
            .FirstOrDefaultAsync(ct);
        if (activeEntry is null)
        {
            return new CredentialDeliveryResult.Denied(CredentialDenialReasons.MaterialUnavailable);
        }

        var material = await domainReadContext.GrantEntryScopes
            .Where(scope => scope.OrganizationId == input.OrganizationId
                            && scope.VaultId == input.VaultId
                            && scope.GrantId == input.GrantId
                            && scope.EntryId == input.EntryId
                            && scope.Envelope != null
                            && scope.Envelope.EntryRevision == activeEntry.CurrentRevision.Value)
            .Select(scope => new
            {
                FieldIds = scope.FieldIds,
                envelope = scope.Envelope!,
            })
            .Select(scope => new
            {
                scope.FieldIds,
                scope.envelope.GrantEnvelopeRevision,
                scope.envelope.EntryRevision,
                scope.envelope.ProtocolVersion,
                scope.envelope.CryptoSuiteId,
                scope.envelope.GrantKeyVersion,
                MemberKeyGeneration = scope.envelope.MemberKeyGeneration.Value,
                RecipientAgentKeyVersion = scope.envelope.RecipientAgentKeyVersion.Value,
                scope.envelope.EncodedSuitePayload,
                scope.envelope.AgentWrappedGrantDek,
                scope.envelope.WrapperSuiteId,
                scope.envelope.AgentKeyFingerprint,
                scope.envelope.ExpiresAt,
                scope.envelope.RemainingUses,
            })
            .FirstOrDefaultAsync(ct);

        // FULL grant covering an entry added after approval: no wrapped DEK exists for this agent yet
        // (the server cannot wrap one). Clients must re-wrap entry keys when grant coverage changes.
        if (material is null)
        {
            if (input.QueryLimit is not null
                && await domainReadContext.Grants.AnyAsync(grant =>
                    grant.Id == input.GrantId
                    && grant.OrganizationId == input.OrganizationId
                    && grant.AgentId == input.AgentId
                    && grant.VaultId == input.VaultId
                    && grant.AgentAccessEpoch == input.AgentAccessEpoch
                    && grant.Status == GrantStatus.Consumed
                    && grant.QueryCount >= grant.QueryLimit,
                    ct))
            {
                return new CredentialDeliveryResult.Denied(CredentialDenialReasons.QueryLimit);
            }
            return new CredentialDeliveryResult.Denied(CredentialDenialReasons.MaterialUnavailable);
        }

        // Entry type remains encrypted, but the already-server-readable FieldIds are committed by
        // the authenticated envelope descriptor. Any delivered script runtime field therefore
        // provides a privacy-compatible structural signal for the exec-only policy without adding
        // a plaintext EntryType column. Reject before incrementing the grant use counter.
        if (input.Method != GrantMethods.Exec
            && material.FieldIds.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Any(fieldId => fieldId.StartsWith("script.", StringComparison.Ordinal)))
        {
            return new CredentialDeliveryResult.Denied(CredentialDenialReasons.ScriptExecOnly);
        }

        // BUG#2 fix: read denormalized names BEFORE the atomic increment / Consumed flip. A DB hiccup
        // on either FirstOrDefaultAsync after the increment would burn the use without delivering the
        // secret. Reading them upfront isolates the increment path from name-resolution failures.
        var agentName = await domainReadContext.Agents
            .Where(a => a.Id == input.AgentId && a.OrganizationId == input.OrganizationId)
            .Select(a => a.Name)
            .FirstOrDefaultAsync(ct);
        // Vault display metadata is encrypted and is never resolved server-side.
        var vaultName = string.Empty;

        int? remainingUses = null;
        var consumed = false;

        if (input.QueryLimit is not null)
        {
            // Atomic check-and-increment with RETURNING. DEVIATION (approved): raw conditional UPDATE
            // rather than the aggregate, because race-safe check-and-increment under concurrent
            // delivery requires the guard to run in the database. No row returned => limit already
            // reached (or grant revoked/expired concurrently) => 429. The last-access stamp (when / IP /
            // hostname) is written in the same statement so it never diverges from the counter.
            // BUG#1 fix: use the REAL post-increment QueryCount from RETURNING (not stale read+1), so
            // the Consumed transition and remainingUses are correct under concurrency.
            var newCount = await domainWriteContext.SqlQuery<int>(
                    $"""
                     WITH updated AS (
                         UPDATE "Grants"
                         SET "QueryCount" = "QueryCount" + 1,
                             "Status" = CASE WHEN "QueryCount" + 1 >= "QueryLimit"
                                             THEN {(int)GrantStatus.Consumed} ELSE "Status" END,
                             "UpdatedAt" = {input.Now}, "LastAccessedAt" = {input.Now},
                             "LastAccessIp" = {input.Ip}, "LastAccessHostname" = {input.Hostname}
                         WHERE "Id" = {input.GrantId}
                           AND "OrganizationId" = {input.OrganizationId}
                           AND "AgentId" = {input.AgentId}
                           AND "VaultId" = {input.VaultId}
                           AND "Status" = {(int)GrantStatus.Active}
                           AND "AgentAccessEpoch" = {input.AgentAccessEpoch}
                           AND EXISTS (
                               SELECT 1 FROM "Agents"
                               WHERE "Id" = {input.AgentId}
                                 AND "OrganizationId" = {input.OrganizationId}
                                 AND "Status" = {(int)AgentStatus.Active}
                                 AND "AccessEpoch" = {input.AgentAccessEpoch})
                           AND EXISTS (
                               SELECT 1
                               FROM "VaultEntries" entry
                               JOIN "GrantEntryEnvelopes" envelope
                                 ON envelope."OrganizationId" = entry."OrganizationId"
                                AND envelope."VaultId" = entry."VaultId"
                                AND envelope."EntryId" = entry."Id"
                               WHERE entry."OrganizationId" = {input.OrganizationId}
                                 AND entry."VaultId" = {input.VaultId}
                                 AND entry."Id" = {input.EntryId}
                                 AND entry."State" = {(int)EntryState.Active}
                                 AND entry."CurrentRevision" = envelope."EntryRevision"
                                 AND envelope."GrantId" = {input.GrantId}
                                 AND envelope."EntryRevision" = {material.EntryRevision}
                                 AND envelope."GrantEnvelopeRevision" = {material.GrantEnvelopeRevision})
                           AND "QueryCount" < "QueryLimit"
                         RETURNING "QueryCount", "Status"
                     ), deleted AS (
                         DELETE FROM "GrantEntryEnvelopes"
                         WHERE "OrganizationId" = {input.OrganizationId}
                           AND "VaultId" = {input.VaultId}
                           AND "GrantId" = {input.GrantId}
                           AND EXISTS (SELECT 1 FROM updated WHERE "Status" = {(int)GrantStatus.Consumed})
                     )
                     SELECT "QueryCount" AS "Value" FROM updated
                     """)
                .ToListAsync(ct);

            if (newCount.Count == 0)
            {
                var currentEpochGrant = await domainReadContext.Grants
                    .Where(grant =>
                        grant.Id == input.GrantId
                        && grant.OrganizationId == input.OrganizationId
                        && grant.AgentId == input.AgentId
                        && grant.VaultId == input.VaultId
                        && grant.AgentAccessEpoch == input.AgentAccessEpoch
                        && domainReadContext.Agents.Any(agent =>
                            agent.Id == input.AgentId
                            && agent.OrganizationId == input.OrganizationId
                            && agent.Status == AgentStatus.Active
                            && agent.AccessEpoch == input.AgentAccessEpoch))
                    .Select(grant => new { grant.Status })
                    .SingleOrDefaultAsync(ct);
                if (currentEpochGrant is null
                    || (currentEpochGrant.Status != GrantStatus.Active
                        && currentEpochGrant.Status != GrantStatus.Consumed))
                {
                    return new CredentialDeliveryResult.Denied(CredentialDenialReasons.NoActiveGrant);
                }

                if (currentEpochGrant.Status == GrantStatus.Active)
                {
                    var materialStillCurrent = await domainReadContext.GrantEntryEnvelopes.AnyAsync(
                        envelope =>
                            envelope.OrganizationId == input.OrganizationId
                            && envelope.VaultId == input.VaultId
                            && envelope.GrantId == input.GrantId
                            && envelope.EntryId == input.EntryId
                            && envelope.EntryRevision == material.EntryRevision
                            && envelope.GrantEnvelopeRevision == material.GrantEnvelopeRevision
                            && domainReadContext.Entries.Any(entry =>
                                entry.OrganizationId == input.OrganizationId
                                && entry.VaultId == input.VaultId
                                && entry.Id == input.EntryId
                                && entry.State == EntryState.Active
                                && entry.CurrentRevision.Value == envelope.EntryRevision),
                        ct);
                    if (!materialStillCurrent)
                    {
                        return new CredentialDeliveryResult.Denied(CredentialDenialReasons.MaterialUnavailable);
                    }
                }

                return new CredentialDeliveryResult.Denied(CredentialDenialReasons.QueryLimit);
            }

            remainingUses = input.QueryLimit.Value - newCount[0];

            // The use that reaches the limit consumes the grant. This retrieve still succeeds.
            consumed = newCount[0] >= input.QueryLimit.Value;
        }
        else
        {
            // Lifetime / time-based grant: no counter, but still record the last-access stamp.
            var updated = await domainWriteContext.ExecuteSqlInterpolatedAsync(
                $"""
                 UPDATE "Grants"
                 SET "LastAccessedAt" = {input.Now}, "LastAccessIp" = {input.Ip}, "LastAccessHostname" = {input.Hostname}
                 WHERE "Id" = {input.GrantId}
                   AND "OrganizationId" = {input.OrganizationId}
                   AND "AgentId" = {input.AgentId}
                   AND "VaultId" = {input.VaultId}
                   AND "Status" = {(int)GrantStatus.Active}
                   AND "AgentAccessEpoch" = {input.AgentAccessEpoch}
                   AND EXISTS (
                       SELECT 1 FROM "Agents"
                       WHERE "Id" = {input.AgentId}
                         AND "OrganizationId" = {input.OrganizationId}
                         AND "Status" = {(int)AgentStatus.Active}
                         AND "AccessEpoch" = {input.AgentAccessEpoch})
                   AND EXISTS (
                       SELECT 1
                       FROM "VaultEntries" entry
                       JOIN "GrantEntryEnvelopes" envelope
                         ON envelope."OrganizationId" = entry."OrganizationId"
                        AND envelope."VaultId" = entry."VaultId"
                        AND envelope."EntryId" = entry."Id"
                       WHERE entry."OrganizationId" = {input.OrganizationId}
                         AND entry."VaultId" = {input.VaultId}
                         AND entry."Id" = {input.EntryId}
                         AND entry."State" = {(int)EntryState.Active}
                         AND entry."CurrentRevision" = envelope."EntryRevision"
                         AND envelope."GrantId" = {input.GrantId}
                         AND envelope."EntryRevision" = {material.EntryRevision}
                         AND envelope."GrantEnvelopeRevision" = {material.GrantEnvelopeRevision})
                 """,
                ct);
            if (updated == 0)
            {
                var activeGrantStillExists = await domainReadContext.Grants.AnyAsync(
                    grant =>
                        grant.Id == input.GrantId
                        && grant.OrganizationId == input.OrganizationId
                        && grant.AgentId == input.AgentId
                        && grant.VaultId == input.VaultId
                        && grant.Status == GrantStatus.Active
                        && grant.AgentAccessEpoch == input.AgentAccessEpoch
                        && domainReadContext.Agents.Any(agent =>
                            agent.Id == input.AgentId
                            && agent.OrganizationId == input.OrganizationId
                            && agent.Status == AgentStatus.Active
                            && agent.AccessEpoch == input.AgentAccessEpoch),
                    ct);
                if (!activeGrantStillExists)
                {
                    return new CredentialDeliveryResult.Denied(CredentialDenialReasons.NoActiveGrant);
                }

                return new CredentialDeliveryResult.Denied(CredentialDenialReasons.MaterialUnavailable);
            }
        }

        return new CredentialDeliveryResult.Granted(
            material.GrantEnvelopeRevision,
            material.EntryRevision,
            material.ProtocolVersion,
            material.CryptoSuiteId,
            material.GrantKeyVersion,
            material.MemberKeyGeneration,
            material.RecipientAgentKeyVersion,
            material.FieldIds.Split('\n', StringSplitOptions.RemoveEmptyEntries),
            material.EncodedSuitePayload,
            material.AgentWrappedGrantDek,
            material.WrapperSuiteId,
            material.AgentKeyFingerprint,
            material.ExpiresAt,
            material.RemainingUses,
            remainingUses,
            consumed,
            agentName ?? CredentialAccessedEvent.UnknownAgent,
            vaultName);
    }
}
